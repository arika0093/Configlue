using System.Security.Cryptography;
using Amazon.AppConfigData;

namespace Configlue.Resource.AwsAppConfig;

/// <summary>
/// Reads one AWS AppConfig configuration profile payload as a versioned resource.
/// </summary>
/// <remarks>
/// <para>
/// In direct mode the payload is retrieved through the AppConfig Data API session model:
/// one <c>StartConfigurationSession</c> call followed by <c>GetLatestConfiguration</c>
/// polls that always use the server-provided next token and wait the server-provided minimum
/// poll interval. Empty poll responses mean the caller already has the latest payload and do
/// not produce change signals, so unchanged configuration skips decode and merge work.
/// </para>
/// <para>
/// In agent mode the payload is retrieved from the local AppConfig Agent endpoint, which
/// manages the service-side session internally. Both modes expose identical Configlue
/// semantics: the profile payload flows through the normal codec pipeline, the session
/// refresh loop drives <see cref="ISourceWatcher"/> invalidation, and the last successfully
/// loaded state is retained through transient failures while health details stay visible via
/// <see cref="GetHealthSnapshot"/> without replacing valid state with an empty payload.
/// </para>
/// <para>v1 is read-only: the resource exposes no writer.</para>
/// </remarks>
public sealed class AwsAppConfigResource
    : IResourceReader,
        ISourceWatcher,
        IResourceIdentity,
        IDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(60);

    /// <summary>The default AppConfig Agent endpoint base address.</summary>
    public static readonly Uri DefaultAgentEndpoint = new(
        "http://localhost:2772/",
        UriKind.Absolute
    );

    private readonly IAwsAppConfigDataClient? _dataClient;
    private readonly IAwsAppConfigAgentFetcher? _agentFetcher;
    private readonly IDisposable? _ownedClient;
    private readonly string _applicationId;
    private readonly string _environmentId;
    private readonly string _configurationProfileId;
    private readonly int? _requiredMinimumPollIntervalInSeconds;
    private readonly TimeSpan _agentPollInterval;
    private readonly ResourceId? _fixedResourceId;
    private readonly ResourceId _resourceId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposeSignal = new();
    private readonly object _healthGate = new();
    private int _disposed;

    private string? _token;
    private TimeSpan _pollInterval = DefaultPollInterval;
    private DateTimeOffset? _lastPollUtc;
    private byte[]? _cachedContent;
    private string? _cachedRevision;
    private DateTimeOffset? _lastAttemptUtc;
    private DateTimeOffset? _lastSuccessUtc;
    private string? _lastError;
    private int _sessionRestarts;
    private long _pollCount;
    private long _changeCount;

    /// <summary>Creates a direct Data API resource. The supplied SDK client remains caller-owned.</summary>
    public AwsAppConfigResource(
        IAmazonAppConfigData client,
        string applicationId,
        string environmentId,
        string configurationProfileId,
        AwsAppConfigResourceOptions? options = null
    )
        : this(
            new AwsAppConfigDataSdkClient(client),
            applicationId,
            environmentId,
            configurationProfileId,
            options,
            ownedClient: null
        ) { }

    /// <summary>Creates an agent-mode resource. The supplied HTTP client remains caller-owned.</summary>
    public AwsAppConfigResource(
        HttpClient httpClient,
        string applicationId,
        string environmentId,
        string configurationProfileId,
        AwsAppConfigResourceOptions? options = null
    )
        : this(
            CreateAgentFetcher(
                httpClient,
                applicationId,
                environmentId,
                configurationProfileId,
                options
            ),
            applicationId,
            environmentId,
            configurationProfileId,
            options,
            ownedClient: null
        ) { }

    internal AwsAppConfigResource(
        IAwsAppConfigDataClient client,
        string applicationId,
        string environmentId,
        string configurationProfileId,
        AwsAppConfigResourceOptions? options = null,
        IDisposable? ownedClient = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateIdentifiers(applicationId, environmentId, configurationProfileId);
        options?.Validate(agentMode: false);

        _dataClient = client;
        _ownedClient = ownedClient;
        _applicationId = applicationId;
        _environmentId = environmentId;
        _configurationProfileId = configurationProfileId;
        _requiredMinimumPollIntervalInSeconds = options?.RequiredMinimumPollIntervalInSeconds;
        _agentPollInterval = options?.AgentPollInterval ?? DefaultPollInterval;
        _fixedResourceId = options?.FixedResourceId;
        _resourceId = CreateResourceId(
            applicationId,
            environmentId,
            configurationProfileId,
            options?.ClientId
        );
        DelayAsync = delayAsync ?? DefaultDelayAsync;
    }

    internal AwsAppConfigResource(
        IAwsAppConfigAgentFetcher fetcher,
        string applicationId,
        string environmentId,
        string configurationProfileId,
        AwsAppConfigResourceOptions? options = null,
        IDisposable? ownedClient = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null
    )
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        ValidateIdentifiers(applicationId, environmentId, configurationProfileId);
        options?.Validate(agentMode: true);

        _agentFetcher = fetcher;
        _ownedClient = ownedClient;
        _applicationId = applicationId;
        _environmentId = environmentId;
        _configurationProfileId = configurationProfileId;
        _agentPollInterval = options?.AgentPollInterval ?? DefaultPollInterval;
        _fixedResourceId = options?.FixedResourceId;
        _resourceId = CreateResourceId(
            applicationId,
            environmentId,
            configurationProfileId,
            options?.ClientId
        );
        DelayAsync = delayAsync ?? DefaultDelayAsync;
    }

    /// <summary>The AppConfig application identifier.</summary>
    public string ApplicationId => _applicationId;

    /// <summary>The AppConfig environment identifier.</summary>
    public string EnvironmentId => _environmentId;

    /// <summary>The AppConfig configuration profile identifier.</summary>
    public string ConfigurationProfileId => _configurationProfileId;

    /// <summary>How this resource retrieves its payload.</summary>
    public AwsAppConfigMode Mode =>
        _dataClient is null ? AwsAppConfigMode.Agent : AwsAppConfigMode.Direct;

    /// <summary>Gets a diagnostic snapshot without affecting loaded state.</summary>
    public AwsAppConfigHealthSnapshot GetHealthSnapshot()
    {
        lock (_healthGate)
        {
            return new AwsAppConfigHealthSnapshot(
                _lastAttemptUtc,
                _lastSuccessUtc,
                _cachedRevision,
                _agentFetcher is null ? _pollInterval : _agentPollInterval,
                _lastError,
                _sessionRestarts,
                _pollCount,
                _changeCount,
                _cachedContent is not null
            );
        }
    }

    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; }

    internal string? CurrentTokenForTests
    {
        get
        {
            lock (_healthGate)
            {
                return _token;
            }
        }
    }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _fixedResourceId ?? _resourceId;

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _dataClient is null
                ? await ReadAgentLockedAsync(cancellationToken).ConfigureAwait(false)
                : await ReadDirectLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(SnapshotRevision(), observedRevision, StringComparison.Ordinal))
        {
            return;
        }

        var first = true;
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            var interval = SnapshotInterval();
            if (!first || SnapshotLastPollUtc() is not null)
            {
                await DelayCancellableAsync(interval, cancellationToken).ConfigureAwait(false);
            }

            first = false;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeSignal.Token
            );
            try
            {
                await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ThrowIfDisposedOrCanceled(cancellationToken);
                throw;
            }

            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var changed = _dataClient is null
                    ? await PollAgentForWatchLockedAsync(observedRevision, linked.Token)
                        .ConfigureAwait(false)
                    : await PollDirectForWatchLockedAsync(observedRevision, linked.Token)
                        .ConfigureAwait(false);
                if (changed)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                ThrowIfDisposedOrCanceled(cancellationToken);
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            _disposeSignal.Cancel();
        }
        catch (ObjectDisposedException exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
        _ownedClient?.Dispose();
        _disposeSignal.Dispose();
    }

    private void ThrowIfDisposedOrCanceled(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async ValueTask<ResourceReadResult> ReadDirectLockedAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            RecordAttempt();
            if (_token is null)
            {
                await RestartDirectLockedAsync(initial: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            AppConfigPollResult result;
            try
            {
                result = await _dataClient!
                    .GetLatestAsync(_token!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AwsAppConfigSessionExpiredException)
            {
                await RestartDirectLockedAsync(initial: false, cancellationToken)
                    .ConfigureAwait(false);
                result = await _dataClient!
                    .GetLatestAsync(_token!, cancellationToken)
                    .ConfigureAwait(false);
            }

            return HandleDirectPollLocked(result);
        }
        catch (AwsAppConfigTransientException exception)
        {
            RecordError(exception.Message);
            return RetainedOrUnavailableLocked();
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                && !cancellationToken.IsCancellationRequested
            )
        {
            RecordError(exception.Message);
            throw;
        }
    }

    private async ValueTask<bool> PollDirectForWatchLockedAsync(
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        try
        {
            RecordAttempt();
            if (_token is null)
            {
                await RestartDirectLockedAsync(initial: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            AppConfigPollResult result;
            try
            {
                result = await _dataClient!
                    .GetLatestAsync(_token!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AwsAppConfigSessionExpiredException)
            {
                await RestartDirectLockedAsync(initial: false, cancellationToken)
                    .ConfigureAwait(false);
                result = await _dataClient!
                    .GetLatestAsync(_token!, cancellationToken)
                    .ConfigureAwait(false);
            }

            return HandleDirectWatchPollLocked(result, observedRevision);
        }
        catch (AwsAppConfigTransientException exception)
        {
            // Transient refresh failures retain the last good state: the watcher keeps waiting
            // on the server-provided interval instead of signaling an empty payload.
            RecordError(exception.Message);
            return false;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                && !cancellationToken.IsCancellationRequested
            )
        {
            RecordError(exception.Message);
            throw;
        }
    }

    private ResourceReadResult HandleDirectPollLocked(AppConfigPollResult result)
    {
        ApplyDirectPollLocked(result.NextToken, result.NextInterval);
        if (!result.Configuration.IsEmpty)
        {
            SetCachedLocked(
                result.Configuration,
                ComputeRevision(result.VersionLabel, result.Configuration)
            );
            RecordSuccess();
            return ResourceReadResult.Success(_cachedContent!, _cachedRevision);
        }

        // An empty configuration means the caller already has the latest payload.
        RecordSuccess();
        return _cachedContent is null
            ? ResourceReadResult.NotFound()
            : ResourceReadResult.Success(_cachedContent, _cachedRevision);
    }

    private bool HandleDirectWatchPollLocked(AppConfigPollResult result, string? observedRevision)
    {
        ApplyDirectPollLocked(result.NextToken, result.NextInterval);
        if (result.Configuration.IsEmpty)
        {
            RecordSuccess();
            return false;
        }

        var revision = ComputeRevision(result.VersionLabel, result.Configuration);
        SetCachedLocked(result.Configuration, revision);
        RecordSuccess();
        return !string.Equals(revision, observedRevision, StringComparison.Ordinal);
    }

    private async ValueTask<ResourceReadResult> ReadAgentLockedAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            RecordAttempt();
            var result = await _agentFetcher!.FetchAsync(cancellationToken).ConfigureAwait(false);
            if (result.NotFound || result.Configuration.IsEmpty)
            {
                RecordSuccess();
                return _cachedContent is null
                    ? ResourceReadResult.NotFound()
                    : ResourceReadResult.Success(_cachedContent, _cachedRevision);
            }

            SetCachedLocked(
                result.Configuration,
                ComputeRevision(result.Version, result.Configuration)
            );
            RecordSuccess();
            return ResourceReadResult.Success(_cachedContent!, _cachedRevision);
        }
        catch (AwsAppConfigTransientException exception)
        {
            RecordError(exception.Message);
            return RetainedOrUnavailableLocked();
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                && !cancellationToken.IsCancellationRequested
            )
        {
            RecordError(exception.Message);
            throw;
        }
    }

    private async ValueTask<bool> PollAgentForWatchLockedAsync(
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        try
        {
            RecordAttempt();
            var result = await _agentFetcher!.FetchAsync(cancellationToken).ConfigureAwait(false);
            if (result.NotFound || result.Configuration.IsEmpty)
            {
                RecordSuccess();
                return false;
            }

            var revision = ComputeRevision(result.Version, result.Configuration);
            var changed = !string.Equals(revision, observedRevision, StringComparison.Ordinal);
            if (
                !string.Equals(revision, _cachedRevision, StringComparison.Ordinal)
                || _cachedContent is null
            )
            {
                SetCachedLocked(result.Configuration, revision);
            }

            RecordSuccess();
            return changed;
        }
        catch (AwsAppConfigTransientException exception)
        {
            RecordError(exception.Message);
            return false;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                && !cancellationToken.IsCancellationRequested
            )
        {
            RecordError(exception.Message);
            throw;
        }
    }

    private ResourceReadResult RetainedOrUnavailableLocked() =>
        _cachedContent is null
            ? ResourceReadResult.Unavailable()
            : ResourceReadResult.Success(_cachedContent, _cachedRevision);

    private async Task RestartDirectLockedAsync(bool initial, CancellationToken cancellationToken)
    {
        var token = await _dataClient!
            .StartSessionAsync(
                _applicationId,
                _environmentId,
                _configurationProfileId,
                _requiredMinimumPollIntervalInSeconds,
                cancellationToken
            )
            .ConfigureAwait(false);
        lock (_healthGate)
        {
            _token = token;
            if (!initial)
            {
                _sessionRestarts++;
            }
        }
    }

    private void ApplyDirectPollLocked(string nextToken, TimeSpan nextInterval)
    {
        lock (_healthGate)
        {
            _token = nextToken;
            _pollInterval = nextInterval < TimeSpan.Zero ? TimeSpan.Zero : nextInterval;
        }
    }

    private void SetCachedLocked(ReadOnlyMemory<byte> content, string revision)
    {
        lock (_healthGate)
        {
            _cachedContent = content.ToArray();
            _cachedRevision = revision;
            _changeCount++;
        }
    }

    private void RecordAttempt()
    {
        lock (_healthGate)
        {
            _lastAttemptUtc = DateTimeOffset.UtcNow;
            _pollCount++;
        }
    }

    private void RecordSuccess()
    {
        lock (_healthGate)
        {
            _lastSuccessUtc = DateTimeOffset.UtcNow;
            _lastPollUtc = DateTimeOffset.UtcNow;
            _lastError = null;
        }
    }

    private void RecordError(string message)
    {
        lock (_healthGate)
        {
            _lastError = message;
            _lastPollUtc = DateTimeOffset.UtcNow;
        }
    }

    private string? SnapshotRevision()
    {
        lock (_healthGate)
        {
            return _cachedRevision;
        }
    }

    private TimeSpan SnapshotInterval()
    {
        lock (_healthGate)
        {
            return _agentFetcher is null ? _pollInterval : _agentPollInterval;
        }
    }

    private DateTimeOffset? SnapshotLastPollUtc()
    {
        lock (_healthGate)
        {
            return _lastPollUtc;
        }
    }

    private async Task DelayCancellableAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeSignal.Token
        );
        try
        {
            await DelayAsync(delay, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }

            throw;
        }
    }

    private static Task DefaultDelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    private static string ComputeRevision(string? versionLabel, ReadOnlyMemory<byte> content)
    {
        if (versionLabel is { Length: > 0 })
        {
            return versionLabel;
        }

        return Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();
    }

    private static void ValidateIdentifiers(
        string applicationId,
        string environmentId,
        string configurationProfileId
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationProfileId);
    }

    internal static IAwsAppConfigAgentFetcher CreateAgentFetcher(
        HttpClient httpClient,
        string applicationId,
        string environmentId,
        string configurationProfileId,
        AwsAppConfigResourceOptions? options
    )
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        var baseAddress = options?.AgentEndpoint ?? DefaultAgentEndpoint;
        return new AwsAppConfigAgentHttpFetcher(
            httpClient,
            AwsAppConfigAgentHttpFetcher.BuildEndpoint(
                baseAddress,
                applicationId,
                environmentId,
                configurationProfileId
            ),
            options?.ClientId
        );
    }

    private static ResourceId CreateResourceId(
        string applicationId,
        string environmentId,
        string configurationProfileId,
        string? clientId
    )
    {
        var identity =
            applicationId
            + "\n"
            + environmentId
            + "\n"
            + configurationProfileId
            + "\n"
            + (clientId ?? string.Empty);
        return new ResourceId(
            "appconfig:"
                + Convert
                    .ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))
                    .ToLowerInvariant()
        );
    }
}
