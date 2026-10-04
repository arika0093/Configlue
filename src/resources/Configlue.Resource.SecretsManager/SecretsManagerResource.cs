using System.Security.Cryptography;
using System.Text;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Configlue.Internal;

namespace Configlue.Resource.SecretsManager;

/// <summary>Reads and writes one versioned payload through AWS Secrets Manager.</summary>
/// <remarks>
/// <para>
/// The secret version ID is exposed as the resource revision. Moving staging labels such as
/// <c>AWSCURRENT</c> change revision on rotation and drive Configlue refresh; fixed version IDs
/// are immutable and never create a watcher.
/// </para>
/// <para>
/// Secrets Manager provides no atomic expected-version precondition, so conditional writes are
/// rejected instead of emulated with a client-side read-before-write check. Writes add a new
/// secret version with unchecked semantics; staging-label movement beyond the configured write
/// labels requires an explicit <see cref="PromoteStagingLabelAsync"/> call.
/// </para>
/// <para>
/// Secret values, binary contents, credentials, KMS details, and decoded typed data never appear
/// in exceptions, revisions, identities, or diagnostics. Only safe metadata (secret ARN or name,
/// version ID, and staging labels) is surfaced.
/// </para>
/// </remarks>
public sealed class SecretsManagerResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        IResourceIdentity,
        ISourceWatcher,
        IDisposable
{
    private readonly ISecretsManagerClient _client;
    private readonly SecretsManagerResourceOptions _options;
    private readonly Func<ConfiglueResourceContext, ISecretsManagerClient>? _clientSelector;
    private readonly WatchShutdown _watchShutdown = new();
    private readonly string _secretId;
    private int _disposed;

    /// <summary>Creates a resource for one secret addressed by ARN or name.</summary>
    /// <remarks>The supplied client remains externally owned and is never disposed.</remarks>
    public SecretsManagerResource(
        IAmazonSecretsManager client,
        string secretId,
        SecretsManagerResourceOptions? options = null
    )
        : this(
            new SecretsManagerClient(client),
            secretId,
            options,
            options?.ClientSelector is { } selector
                ? context => new SecretsManagerClient(selector(context))
                : null
        ) { }

    internal SecretsManagerResource(
        ISecretsManagerClient client,
        string secretId,
        SecretsManagerResourceOptions? options = null,
        Func<ConfiglueResourceContext, ISecretsManagerClient>? clientSelector = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);

        _client = client;
        _options = options ?? new SecretsManagerResourceOptions();
        _options.Validate();
        _clientSelector = clientSelector;
        _secretId = secretId;
    }

    /// <summary>The secret ARN or name.</summary>
    public string SecretId => _secretId;

    /// <summary>Whether this resource can expose a change watcher for its configured selection.</summary>
    /// <remarks>Fixed versions are immutable, so they never support watching.</remarks>
    public bool IsWatchSupported => _options.VersionId is null && _options.WatchEnabled;

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context)
    {
        if (_options.FixedResourceId is { } explicitId)
        {
            return explicitId;
        }

        var selection = ResolveSelection(context);
        return CreateResourceId(
            selection.SecretId,
            selection.VersionId is { } versionId
                ? "version:" + versionId
                : "stage:" + selection.VersionStage,
            _clientSelector is null ? null : context.Route.Value
        );
    }

    /// <inheritdoc />
    public bool IsPipelineReadPreferred => true;

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var result = await ReadAsync(context, cancellationToken).ConfigureAwait(false);
        return await PipelineResourceReader
            .FromMemoryAsync(result, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        var selection = ResolveSelection(context);
        var client = GetClient(context);
        SecretsManagerSecretValue value;
        try
        {
            value = await ExecuteWithRetryAsync(
                    token =>
                        client.GetSecretValueAsync(
                            selection.SecretId,
                            selection.VersionId,
                            selection.VersionStage,
                            token
                        ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (ResourceNotFoundException)
        {
            return ResourceReadResult.NotFound();
        }
        catch (InvalidRequestException exception) when (IsDeletedSecret(exception))
        {
            return ResourceReadResult.NotFound();
        }
        catch (DecryptionFailureException)
        {
            return ResourceReadResult.Unavailable();
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            return ResourceReadResult.Unavailable();
        }

        if (value.SecretString is { } secretString)
        {
            return ResourceReadResult.Success(
                Encoding.UTF8.GetBytes(secretString),
                value.VersionId
            );
        }

        if (value.SecretBinary is { } secretBinary)
        {
            return ResourceReadResult.Success(secretBinary.ToArray(), value.VersionId);
        }

        return ResourceReadResult.InvalidPayload(value.VersionId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Adds a new secret version with unchecked semantics. Conditional writes are rejected
    /// because Secrets Manager offers no atomic expected-version precondition.
    /// </remarks>
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        var selection = ResolveSelection(context);
        var client = GetClient(context);
        if (!request.Condition.IsNone)
        {
            throw new InvalidOperationException(
                $"The Secrets Manager secret '{selection.SecretId}' does not support atomic conditional writes."
            );
        }

        var clientRequestToken = Guid.NewGuid().ToString("N");
        SecretsManagerPutResult result;
        try
        {
            result = await ExecuteWithRetryAsync(
                    token =>
                        client.PutSecretValueAsync(
                            selection.SecretId,
                            request.Content,
                            _options.UseSecretBinary,
                            _options.WriteVersionStages,
                            clientRequestToken,
                            token
                        ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (ResourceNotFoundException)
        {
            throw new InvalidOperationException(
                $"The Secrets Manager secret '{selection.SecretId}' was not found."
            );
        }

        return new StateWriteResult(result.VersionId);
    }

    /// <summary>
    /// Explicitly moves a staging label to a secret version using normal
    /// Secrets Manager promotion semantics.
    /// </summary>
    public async ValueTask PromoteStagingLabelAsync(
        string versionId,
        string versionStage,
        ConfiglueResourceContext context = default,
        string? removeFromVersionId = null,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionStage);
        if (removeFromVersionId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(removeFromVersionId);
        }

        var selection = ResolveSelection(context);
        var client = GetClient(context);
        try
        {
            await ExecuteWithRetryAsync(
                    token =>
                        client.UpdateSecretVersionStageAsync(
                            selection.SecretId,
                            versionStage,
                            versionId,
                            removeFromVersionId,
                            token
                        ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (ResourceNotFoundException)
        {
            throw new InvalidOperationException(
                $"The Secrets Manager secret '{selection.SecretId}' was not found."
            );
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Polls version metadata (<c>DescribeSecret</c>) at <see cref="SecretsManagerResourceOptions.PollingInterval"/>
    /// intervals and only returns after the caller re-reads the full payload. The secret value is
    /// never fetched by the watcher itself. Fixed versions are immutable and throw.
    /// </remarks>
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        var selection = ResolveSelection(context);
        if (selection.VersionId is not null)
        {
            throw new InvalidOperationException(
                $"The Secrets Manager secret '{selection.SecretId}' is pinned to a fixed version and does not support watching."
            );
        }

        if (!_options.WatchEnabled)
        {
            throw new InvalidOperationException(
                $"Watching is disabled for the Secrets Manager secret '{selection.SecretId}'."
            );
        }

        await _watchShutdown
            .WaitAsync(
                watchCancellationToken =>
                    PollUntilChangedAsync(
                        GetClient(context),
                        selection.SecretId,
                        selection.VersionStage!,
                        observedRevision,
                        watchCancellationToken
                    ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watchShutdown.Signal();
    }

    private async ValueTask PollUntilChangedAsync(
        ISecretsManagerClient client,
        string secretId,
        string versionStage,
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? currentVersion;
            try
            {
                var description = await ExecuteWithRetryAsync(
                        token => client.DescribeSecretAsync(secretId, token),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (description.DeletedDate is not null)
                {
                    return;
                }

                currentVersion = FindVersionForStage(description, versionStage);
            }
            catch (ResourceNotFoundException)
            {
                return;
            }
            catch (InvalidRequestException exception) when (IsDeletedSecret(exception))
            {
                return;
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                await Task.Delay(_options.PollingInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (
                currentVersion is null
                || !string.Equals(currentVersion, observedRevision, StringComparison.Ordinal)
            )
            {
                return;
            }

            await Task.Delay(_options.PollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ExecuteWithRetryAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken
    )
    {
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await operation(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
                when (IsTransient(exception) && attempts < _options.MaxRetryAttempts)
            {
                attempts++;
                await Task.Delay(ComputeRetryDelay(attempts), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken
    )
    {
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
                when (IsTransient(exception) && attempts < _options.MaxRetryAttempts)
            {
                attempts++;
                await Task.Delay(ComputeRetryDelay(attempts), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private TimeSpan ComputeRetryDelay(int attempt)
    {
        var multiplier = 1L << Math.Min(attempt - 1, 10);
        var ticks = _options.RetryBaseDelay.Ticks * multiplier;
        return new TimeSpan(Math.Min(ticks, TimeSpan.FromSeconds(5).Ticks));
    }

    private ISecretsManagerClient GetClient(ConfiglueResourceContext context) =>
        _clientSelector?.Invoke(context) ?? _client;

    private SecretsManagerSelection ResolveSelection(ConfiglueResourceContext context)
    {
        var secretId = _options.SecretIdSelector?.Invoke(context) ?? _secretId;
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        var versionId = _options.VersionIdSelector?.Invoke(context) ?? _options.VersionId;
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            return new SecretsManagerSelection(secretId, versionId, null);
        }

        var versionStage = _options.VersionStageSelector?.Invoke(context) ?? _options.VersionStage;
        ArgumentException.ThrowIfNullOrWhiteSpace(versionStage);
        return new SecretsManagerSelection(secretId, null, versionStage);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static string? FindVersionForStage(
        SecretsManagerSecretDescription description,
        string versionStage
    )
    {
        return description
            .VersionIdsToStages.Where(entry =>
                entry.Value.Any(stage =>
                    string.Equals(stage, versionStage, StringComparison.Ordinal)
                )
            )
            .Select(static entry => entry.Key)
            .FirstOrDefault();
    }

    private static bool IsDeletedSecret(InvalidRequestException exception) =>
        exception.Message.IndexOf("delet", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsTransient(Exception exception) =>
        exception is LimitExceededException
        || exception is InternalServiceErrorException
        || (
            exception is AmazonSecretsManagerException serviceException
            && (
                serviceException.ErrorCode
                    is "Throttling"
                        or "ThrottlingException"
                        or "Throttled"
                        or "ThrottledException"
                        or "TooManyRequestsException"
                        or "RequestLimitExceeded"
                        or "RequestThrottled"
                        or "RequestThrottledException"
                        or "ProvisionedThroughputExceededException"
                        or "PriorRequestNotComplete"
                || serviceException.StatusCode
                    is (System.Net.HttpStatusCode)429
                        or System.Net.HttpStatusCode.InternalServerError
                        or System.Net.HttpStatusCode.BadGateway
                        or System.Net.HttpStatusCode.ServiceUnavailable
                        or System.Net.HttpStatusCode.GatewayTimeout
            )
        );

    private static ResourceId CreateResourceId(
        string secretId,
        string versionSelector,
        string? route = null
    )
    {
        var identity = Encoding.UTF8.GetBytes(
            route is null
                ? secretId + "\n" + versionSelector
                : secretId + "\n" + versionSelector + "\n" + route
        );
        return new ResourceId(
            $"secretsmanager:{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}"
        );
    }

    private sealed record SecretsManagerSelection(
        string SecretId,
        string? VersionId,
        string? VersionStage
    );

    private sealed class SecretsManagerClient : ISecretsManagerClient
    {
        private readonly IAmazonSecretsManager _client;

        public SecretsManagerClient(IAmazonSecretsManager client)
        {
            ArgumentNullException.ThrowIfNull(client);
            _client = client;
        }

        public async ValueTask<SecretsManagerSecretValue> GetSecretValueAsync(
            string secretId,
            string? versionId,
            string? versionStage,
            CancellationToken cancellationToken
        )
        {
            var request = new GetSecretValueRequest { SecretId = secretId };
            if (versionId is not null)
            {
                request.VersionId = versionId;
            }
            else
            {
                request.VersionStage = versionStage;
            }

            var getResponse = await _client
                .GetSecretValueAsync(request, cancellationToken)
                .ConfigureAwait(false);
            ReadOnlyMemory<byte>? binary = getResponse.SecretBinary is { } stream
                ? stream.ToArray()
                : null;
            return new SecretsManagerSecretValue(
                getResponse.SecretString,
                binary,
                getResponse.VersionId,
                getResponse.VersionStages ?? [],
                getResponse.ARN,
                getResponse.Name
            );
        }

        public async ValueTask<SecretsManagerSecretDescription> DescribeSecretAsync(
            string secretId,
            CancellationToken cancellationToken
        )
        {
            var describeResponse = await _client
                .DescribeSecretAsync(
                    new DescribeSecretRequest { SecretId = secretId },
                    cancellationToken
                )
                .ConfigureAwait(false);
            var stages = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (describeResponse.VersionIdsToStages is { } versionIdsToStages)
            {
                foreach (var entry in versionIdsToStages)
                {
                    stages[entry.Key] = entry.Value ?? [];
                }
            }

            return new SecretsManagerSecretDescription(
                describeResponse.ARN,
                describeResponse.Name,
                stages,
                describeResponse.DeletedDate
            );
        }

        public async ValueTask<SecretsManagerPutResult> PutSecretValueAsync(
            string secretId,
            ReadOnlyMemory<byte> content,
            bool asBinary,
            IReadOnlyList<string> versionStages,
            string clientRequestToken,
            CancellationToken cancellationToken
        )
        {
            var request = new PutSecretValueRequest
            {
                SecretId = secretId,
                ClientRequestToken = clientRequestToken,
                VersionStages = [.. versionStages],
            };
            if (asBinary)
            {
                request.SecretBinary = new MemoryStream(content.ToArray());
            }
            else
            {
                request.SecretString = Encoding.UTF8.GetString(content.Span);
            }

            var putResponse = await _client
                .PutSecretValueAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return new SecretsManagerPutResult(
                putResponse.VersionId,
                putResponse.ARN,
                putResponse.Name,
                putResponse.VersionStages ?? []
            );
        }

        public async ValueTask UpdateSecretVersionStageAsync(
            string secretId,
            string versionStage,
            string moveToVersionId,
            string? removeFromVersionId,
            CancellationToken cancellationToken
        )
        {
            var request = new UpdateSecretVersionStageRequest
            {
                SecretId = secretId,
                VersionStage = versionStage,
                MoveToVersionId = moveToVersionId,
            };
            if (removeFromVersionId is not null)
            {
                request.RemoveFromVersionId = removeFromVersionId;
            }

            await _client
                .UpdateSecretVersionStageAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

internal interface ISecretsManagerClient
{
    ValueTask<SecretsManagerSecretValue> GetSecretValueAsync(
        string secretId,
        string? versionId,
        string? versionStage,
        CancellationToken cancellationToken
    );

    ValueTask<SecretsManagerSecretDescription> DescribeSecretAsync(
        string secretId,
        CancellationToken cancellationToken
    );

    ValueTask<SecretsManagerPutResult> PutSecretValueAsync(
        string secretId,
        ReadOnlyMemory<byte> content,
        bool asBinary,
        IReadOnlyList<string> versionStages,
        string clientRequestToken,
        CancellationToken cancellationToken
    );

    ValueTask UpdateSecretVersionStageAsync(
        string secretId,
        string versionStage,
        string moveToVersionId,
        string? removeFromVersionId,
        CancellationToken cancellationToken
    );
}

internal sealed record SecretsManagerSecretValue(
    string? SecretString,
    ReadOnlyMemory<byte>? SecretBinary,
    string? VersionId,
    IReadOnlyList<string> VersionStages,
    string? Arn,
    string? Name
);

internal sealed record SecretsManagerSecretDescription(
    string? Arn,
    string? Name,
    IReadOnlyDictionary<string, IReadOnlyList<string>> VersionIdsToStages,
    DateTime? DeletedDate
);

internal sealed record SecretsManagerPutResult(
    string? VersionId,
    string? Arn,
    string? Name,
    IReadOnlyList<string> VersionStages
);
