using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Configlue.Sources;

namespace Configlue.Resource.GoogleSecretManager;

/// <summary>The lifecycle state of a Secret Manager version, without payload data.</summary>
public enum GoogleSecretVersionState
{
    /// <summary>The version is usable for reads.</summary>
    Enabled = 0,

    /// <summary>The version exists but cannot be accessed until re-enabled.</summary>
    Disabled = 1,

    /// <summary>The version is scheduled for destruction or destroyed.</summary>
    Destroyed = 2,
}

/// <summary>Safe metadata for one Secret Manager version. Never carries payload bytes.</summary>
public sealed record GoogleSecretVersionMetadata
{
    /// <summary>Creates version metadata.</summary>
    public GoogleSecretVersionMetadata(
        string versionName,
        string versionId,
        GoogleSecretVersionState state
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        VersionName = versionName;
        VersionId = versionId;
        State = state;
    }

    /// <summary>The full version resource name, for example <c>projects/p/secrets/s/versions/3</c>.</summary>
    public string VersionName { get; init; }

    /// <summary>The resolved numeric version ID, for example <c>"3"</c>. Safe for revisions.</summary>
    public string VersionId { get; init; }

    /// <summary>The lifecycle state.</summary>
    public GoogleSecretVersionState State { get; init; }
}

/// <summary>The payload and resolved identity of one accessed secret version.</summary>
public sealed record GoogleSecretAccessResult
{
    /// <summary>Creates an access result.</summary>
    public GoogleSecretAccessResult(
        ReadOnlyMemory<byte> payload,
        string versionName,
        string versionId
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        Payload = payload;
        VersionName = versionName;
        VersionId = versionId;
    }

    /// <summary>The secret payload bytes.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>The full version resource name that served the payload.</summary>
    public string VersionName { get; init; }

    /// <summary>The resolved numeric version ID. Safe for revisions.</summary>
    public string VersionId { get; init; }
}

/// <summary>Base error for Secret Manager failures. Messages carry only resource names, never payloads.</summary>
public class GoogleSecretManagerException : Exception
{
    /// <summary>Creates a Secret Manager error.</summary>
    public GoogleSecretManagerException(string message)
        : base(message) { }

    /// <summary>Creates a Secret Manager error with an inner cause.</summary>
    public GoogleSecretManagerException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>The secret or selected version does not exist.</summary>
public sealed class GoogleSecretNotFoundException : GoogleSecretManagerException
{
    /// <summary>Creates a not-found error.</summary>
    public GoogleSecretNotFoundException(string message)
        : base(message) { }

    /// <summary>Creates a not-found error with an inner cause.</summary>
    public GoogleSecretNotFoundException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>The selected version is disabled or destroyed and cannot serve payloads.</summary>
public sealed class GoogleSecretVersionDisabledException : GoogleSecretManagerException
{
    /// <summary>Creates a disabled-version error.</summary>
    public GoogleSecretVersionDisabledException(string message)
        : base(message) { }

    /// <summary>Creates a disabled-version error with an inner cause.</summary>
    public GoogleSecretVersionDisabledException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>The Secret Manager backend is temporarily unavailable (transport, timeout, or 5xx).</summary>
public sealed class GoogleSecretUnavailableException : GoogleSecretManagerException
{
    /// <summary>Creates an unavailability error.</summary>
    public GoogleSecretUnavailableException(string message)
        : base(message) { }

    /// <summary>Creates an unavailability error with an inner cause.</summary>
    public GoogleSecretUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Minimal Secret Manager client surface used by <see cref="GoogleSecretManagerResource"/>.
/// Implementations remain caller-owned and must never include payload bytes, access tokens,
/// or credential material in thrown messages.
/// </summary>
/// <remarks>
/// <para>
/// External implementation scenario: this interface is the supported seam for Google Secret
/// Manager because this package intentionally takes no dependency on a Google client library.
/// Adapt <c>Google.Cloud.SecretManager.V1.SecretManagerServiceClient</c>
/// (<c>AccessSecretVersion</c> maps to <see cref="AccessSecretVersionAsync"/>,
/// <c>GetSecretVersion</c> maps to <see cref="GetSecretVersionAsync"/>, and
/// <c>AddSecretVersion</c> maps to <see cref="AddSecretVersionAsync"/>), a REST pipeline
/// with its own token handling, or an emulator for local development. Application Default
/// Credentials are supplied by constructing that client with ADC (for example via
/// <c>SecretManagerServiceClient.Create()</c>) and injecting it through the resource constructors
/// or a <c>ClientFactory</c>; this package never captures credentials itself.
/// </para>
/// <para>
/// The contract is intentionally small (access, metadata, append) and carries only Configlue
/// revisions and errors. Tests implement this interface with in-memory fakes.
/// </para>
/// </remarks>
public interface IGoogleSecretManagerClient
{
    /// <summary>Downloads the payload for one versioned secret name.</summary>
    Task<GoogleSecretAccessResult> AccessSecretVersionAsync(
        string versionedName,
        CancellationToken cancellationToken
    );

    /// <summary>Reads lightweight version metadata without downloading the payload.</summary>
    Task<GoogleSecretVersionMetadata> GetSecretVersionAsync(
        string versionedName,
        CancellationToken cancellationToken
    );

    /// <summary>Appends a new immutable version to the parent secret.</summary>
    Task<GoogleSecretVersionMetadata> AddSecretVersionAsync(
        string parent,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken
    );
}

/// <summary>Reads and writes one binary resource through a Google Cloud Secret Manager secret.</summary>
/// <remarks>
/// <para>
/// One secret version is treated as a binary resource and composed with the normal Configlue
/// Codec pipeline. The resolved numeric version ID is exposed as the resource revision; the
/// requested selector (<c>"latest"</c>, an alias, or a fixed number) selects which version is read.
/// Fixed numeric versions are immutable; aliases such as <c>"latest"</c> may move.
/// </para>
/// <para>
/// Reads require <c>secretmanager.versions.access</c>; metadata-only polling additionally uses
/// <c>secretmanager.versions.get</c>. Adding versions requires <c>secretmanager.versions.add</c>.
/// Writes are opt-in (see <see cref="GoogleSecretManagerResourceOptions.EnableWrites"/>) and always
/// append a new version; aliases and secret metadata are never moved or mutated by writes, and no
/// compare-and-swap semantics are claimed because <c>AddSecretVersion</c> cannot atomically
/// enforce an expected baseline. Revision preconditions are re-checked on a best-effort basis
/// with a metadata read immediately before appending.
/// </para>
/// </remarks>
public sealed class GoogleSecretManagerResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        IResourceIdentity,
        ISourceWatcher,
        IDisposable
{
    /// <summary>The moving alias that resolves to the latest enabled version.</summary>
    public const string LatestVersion = "latest";

    private readonly IGoogleSecretManagerClient _client;
    private readonly GoogleSecretManagerResourceOptions _options;
    private readonly Func<ConfiglueResourceContext, IGoogleSecretManagerClient>? _clientSelector;
    private readonly TaskCompletionSource _disposedSignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _disposed;

    /// <summary>Creates a resource for one secret.</summary>
    public GoogleSecretManagerResource(
        IGoogleSecretManagerClient client,
        string projectId,
        string secretId,
        GoogleSecretManagerResourceOptions? options = null
    )
        : this(
            client,
            projectId,
            secretId,
            options,
            options?.ClientSelector is { } selector ? context => selector(context) : null
        ) { }

    internal GoogleSecretManagerResource(
        IGoogleSecretManagerClient client,
        string projectId,
        string secretId,
        GoogleSecretManagerResourceOptions? options,
        Func<ConfiglueResourceContext, IGoogleSecretManagerClient>? clientSelector
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        options?.Validate();

        _client = client;
        _options = options ?? new GoogleSecretManagerResourceOptions();
        _clientSelector = clientSelector;
        ProjectId = projectId;
        SecretId = secretId;
    }

    /// <summary>The Google Cloud project ID.</summary>
    public string ProjectId { get; }

    /// <summary>The secret ID.</summary>
    public string SecretId { get; }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _options.FixedResourceId
        ?? CreateResourceId(
            ResolveProjectId(context),
            ResolveLocation(context),
            ResolveSecretId(context),
            ResolveVersion(context),
            _clientSelector is null ? null : context.Route.Value
        );

    /// <inheritdoc />
    public bool IsPipelineReadPreferred => true;

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var result = await ReadAsync(context, cancellationToken).ConfigureAwait(false);
        if (result.Status != StateReadStatus.Success)
        {
            return result.Status switch
            {
                StateReadStatus.NotFound => PipelineResourceReadResult.NotFound(result.Revision),
                StateReadStatus.Unavailable => PipelineResourceReadResult.Unavailable(
                    result.Revision
                ),
                _ => PipelineResourceReadResult.InvalidPayload(result.Revision),
            };
        }

        var content = CreateReadOnlyStream(result.Content);
        return PipelineResourceReader.FromStream(content, result.Revision, result.Schema);
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var versionedName = BuildVersionedName(context);
        try
        {
            var result = await GetClient(context)
                .AccessSecretVersionAsync(versionedName, cancellationToken)
                .ConfigureAwait(false);
            return ResourceReadResult.Success(result.Payload, result.VersionId);
        }
        catch (GoogleSecretNotFoundException)
        {
            return ResourceReadResult.NotFound();
        }
        catch (GoogleSecretVersionDisabledException)
        {
            return ResourceReadResult.NotFound();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Revision conditions are verified on a best-effort basis with a metadata read immediately
    /// before appending: a stale baseline raises <see cref="StateConflictException"/> without
    /// creating a version. Secret Manager cannot enforce the baseline atomically, so concurrent
    /// writers may still interleave; no compare-and-swap semantics are claimed.
    /// </remarks>
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (!_options.EnableWrites)
        {
            throw new InvalidOperationException(
                "Secret Manager writes are opt-in. Set EnableWrites to add new secret versions."
            );
        }

        var parent = BuildParentName(context);
        if (!request.Condition.IsNone)
        {
            var baseline = await GetBaselineAsync(context, cancellationToken).ConfigureAwait(false);
            if (!request.Condition.IsSatisfiedBy(baseline.Revision, baseline.Exists))
            {
                throw new StateConflictException(
                    $"The secret '{parent}' changed after it was read. "
                        + "Writes append new versions and cannot atomically enforce the expected baseline."
                );
            }
        }

        var metadata = await GetClient(context)
            .AddSecretVersionAsync(parent, request.Content, cancellationToken)
            .ConfigureAwait(false);
        return new StateWriteResult(metadata.VersionId);
    }

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (IsFixedNumericVersion(ResolveVersion(context)))
        {
            return WaitForShutdownAsync(cancellationToken);
        }

        return PollForAliasChangeAsync(context, observedRevision, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposedSignal.TrySetResult();
    }

    /// <summary>Whether a version selector addresses one immutable numeric version.</summary>
    public static bool IsFixedNumericVersion(string version) =>
        !string.IsNullOrWhiteSpace(version)
        && version.All(static character => character >= '0' && character <= '9');

    /// <summary>Builds the parent secret name for the given identifiers.</summary>
    public static string BuildParentName(string projectId, string? location, string secretId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        if (location is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(location);
            return $"projects/{projectId}/locations/{location}/secrets/{secretId}";
        }

        return $"projects/{projectId}/secrets/{secretId}";
    }

    /// <summary>Builds a versioned secret name for the given identifiers.</summary>
    public static string BuildVersionedName(
        string projectId,
        string? location,
        string secretId,
        string version
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return $"{BuildParentName(projectId, location, secretId)}/versions/{version}";
    }

    private async ValueTask WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(_disposedSignal.Task, DelayForever(cancellationToken))
            .ConfigureAwait(false);
        if (completed != _disposedSignal.Task)
        {
            await completed.ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async ValueTask PollForAliasChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        var versionedName = BuildVersionedName(context);
        var pollInterval = _options.PollInterval;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposedSignal.Task.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            GoogleSecretVersionMetadata metadata;
            try
            {
                metadata = await GetClient(context)
                    .GetSecretVersionAsync(versionedName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GoogleSecretNotFoundException) when (!cancellationToken.IsCancellationRequested)
            {
                if (observedRevision is not null)
                {
                    return;
                }

                await DelayOrShutdownAsync(pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (GoogleSecretVersionDisabledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                if (observedRevision is not null)
                {
                    return;
                }

                await DelayOrShutdownAsync(pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (GoogleSecretUnavailableException)
                when (!cancellationToken.IsCancellationRequested)
            {
                await DelayOrShutdownAsync(pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (
                metadata.State
                is GoogleSecretVersionState.Disabled
                    or GoogleSecretVersionState.Destroyed
            )
            {
                if (observedRevision is not null)
                {
                    return;
                }
            }
            else if (!string.Equals(metadata.VersionId, observedRevision, StringComparison.Ordinal))
            {
                return;
            }

            await DelayOrShutdownAsync(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DelayOrShutdownAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(
                Task.Delay(delay, cancellationToken),
                _disposedSignal.Task
            )
            .ConfigureAwait(false);
        if (completed == _disposedSignal.Task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        await completed.ConfigureAwait(false);
    }

    private static Task DelayForever(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken);

    private IGoogleSecretManagerClient GetClient(ConfiglueResourceContext context) =>
        _clientSelector?.Invoke(context) ?? _client;

    private async Task<SecretBaseline> GetBaselineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken
    )
    {
        var versionedName = BuildVersionedName(context);
        GoogleSecretVersionMetadata metadata;
        try
        {
            metadata = await GetClient(context)
                .GetSecretVersionAsync(versionedName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GoogleSecretNotFoundException)
        {
            return new SecretBaseline(null, Exists: false);
        }
        catch (GoogleSecretVersionDisabledException)
        {
            return new SecretBaseline(null, Exists: false);
        }

        return metadata.State is GoogleSecretVersionState.Enabled
            ? new SecretBaseline(metadata.VersionId, Exists: true)
            : new SecretBaseline(null, Exists: false);
    }

    private readonly record struct SecretBaseline(string? Revision, bool Exists);

    private string BuildParentName(ConfiglueResourceContext context) =>
        BuildParentName(
            ResolveProjectId(context),
            ResolveLocation(context),
            ResolveSecretId(context)
        );

    private string BuildVersionedName(ConfiglueResourceContext context) =>
        BuildVersionedName(
            ResolveProjectId(context),
            ResolveLocation(context),
            ResolveSecretId(context),
            ResolveVersion(context)
        );

    private string ResolveProjectId(ConfiglueResourceContext context)
    {
        var projectId = _options.ProjectIdSelector?.Invoke(context) ?? ProjectId;
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return projectId;
    }

    private string ResolveSecretId(ConfiglueResourceContext context)
    {
        var secretId = _options.SecretIdSelector?.Invoke(context) ?? SecretId;
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        return secretId;
    }

    private string? ResolveLocation(ConfiglueResourceContext context)
    {
        var location = _options.LocationSelector?.Invoke(context) ?? _options.Location;
        if (location is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(location);
        }

        return location;
    }

    private string ResolveVersion(ConfiglueResourceContext context)
    {
        var version = _options.VersionSelector?.Invoke(context) ?? _options.Version;
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return version;
    }

    private static MemoryStream CreateReadOnlyStream(ReadOnlyMemory<byte> content)
    {
        if (MemoryMarshal.TryGetArray(content, out var segment) && segment.Array is not null)
        {
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false);
        }

        return new MemoryStream(content.ToArray(), writable: false);
    }

    private static ResourceId CreateResourceId(
        string projectId,
        string? location,
        string secretId,
        string version,
        string? route = null
    )
    {
        var identity = Encoding.UTF8.GetBytes(
            string.Join(
                "\n",
                projectId,
                location ?? string.Empty,
                secretId,
                version,
                route ?? string.Empty
            )
        );
        return new ResourceId(
            $"googlesecrets:{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}"
        );
    }
}
