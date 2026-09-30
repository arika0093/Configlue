using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Configlue.Sources;

namespace Configlue.Resource.Http;

/// <summary>Reads and watches a byte resource exposed through the Configlue HTTP resource protocol.</summary>
public sealed class HttpResourceReader
    : IResourceReader,
        IContextualPipelineResourceReader,
        IContextualSourceWatcher,
        ITryContextualResourceIdentity
{
    /// <summary>Response and request header carrying the source schema identifier.</summary>
    public const string SchemaIdHeaderName = "Configlue-Schema-Id";

    /// <summary>Response and request header carrying the source schema version.</summary>
    public const string SchemaVersionHeaderName = "Configlue-Schema-Version";

    private readonly HttpClient _httpClient;
    private readonly Uri _getUri;
    private readonly Uri _updateUri;
    private readonly Func<ConfiglueResourceContext, Uri>? _endpointRootSelector;
    private readonly HttpResourceOptions _fixedOptions;
    private readonly ResourceId? _configuredResourceId;
    private readonly ConcurrentDictionary<string, ContextWatcher> _contextWatchers = new(
        StringComparer.Ordinal
    );
    private readonly string _contentType;
    private readonly TimeSpan _pollingInterval;
    private readonly TimeSpan _maximumPollingInterval;
    private readonly TimeSpan _requestTimeout;
    private readonly object _snapshotGate = new();
    private readonly object _watchGate = new();
    private readonly List<ChangeWaiter> _changeWaiters = [];
    private CancellationTokenSource? _sharedWatchCancellation;
    private Task? _sharedWatchTask;
    private HttpResourceSnapshot? _lastSnapshot;

    /// <summary>Creates a resource reader for the supplied HTTP endpoint root.</summary>
    public HttpResourceReader(
        HttpClient httpClient,
        Uri endpointRoot,
        HttpResourceOptions? options = null,
        ResourceId? resourceId = null
    )
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpointRoot);
        if (
            !endpointRoot.IsAbsoluteUri
            || (
                endpointRoot.Scheme != Uri.UriSchemeHttp
                && endpointRoot.Scheme != Uri.UriSchemeHttps
            )
        )
        {
            throw new ArgumentException(
                "The endpoint root must be an absolute HTTP or HTTPS URI.",
                nameof(endpointRoot)
            );
        }

        if (
            endpointRoot.UserInfo.Length > 0
            || endpointRoot.Query.Length > 0
            || endpointRoot.Fragment.Length > 0
        )
        {
            throw new ArgumentException(
                "Credentials, query parameters, and fragments must be configured on HttpClient or endpoint paths, not the endpoint root.",
                nameof(endpointRoot)
            );
        }

        var configuredOptions = options ?? new HttpResourceOptions();
        _endpointRootSelector = configuredOptions.EndpointRootSelector;
        _fixedOptions = new HttpResourceOptions
        {
            GetPath = configuredOptions.GetPath,
            UpdatePath = configuredOptions.UpdatePath,
            ContentType = configuredOptions.ContentType,
            PollingInterval = configuredOptions.PollingInterval,
            MaximumPollingInterval = configuredOptions.MaximumPollingInterval,
            RequestTimeout = configuredOptions.RequestTimeout,
        };
        _configuredResourceId = resourceId;
        _httpClient = httpClient;
        var root = EnsureTrailingSlash(endpointRoot);
        _getUri = CombineEndpoint(root, configuredOptions.GetPath, nameof(options));
        _updateUri = CombineEndpoint(root, configuredOptions.UpdatePath, nameof(options));
        if (configuredOptions.PollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "PollingInterval must be greater than zero."
            );
        }

        if (configuredOptions.MaximumPollingInterval < configuredOptions.PollingInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumPollingInterval must be greater than or equal to PollingInterval."
            );
        }

        if (configuredOptions.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "RequestTimeout must be greater than zero."
            );
        }

        if (!MediaTypeHeaderValue.TryParse(configuredOptions.ContentType, out var contentType))
        {
            throw new ArgumentException("ContentType must be a valid media type.", nameof(options));
        }

        _contentType = contentType.ToString();
        _pollingInterval = configuredOptions.PollingInterval;
        _maximumPollingInterval = configuredOptions.MaximumPollingInterval;
        _requestTimeout = configuredOptions.RequestTimeout;
        ResourceId =
            resourceId
            ?? (
                _endpointRootSelector is null
                    ? CreateResourceId(root, _getUri, _updateUri)
                    : CreateContextReader(ConfiglueResourceContext.Default).ResourceId
            );
    }

    /// <inheritdoc />
    public ResourceId ResourceId { get; }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _endpointRootSelector is null ? ResourceId : CreateContextReader(context).ResourceId;

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        resourceId = GetResourceId(context);
        return true;
    }

    /// <summary>The GET endpoint used to read the resource.</summary>
    public Uri GetUri => _getUri;

    /// <inheritdoc />
    public bool IsPipelineReadPreferred => true;

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        CancellationToken cancellationToken = default
    ) =>
        await ReadPipelineAsync(ConfiglueResourceContext.Default, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (_endpointRootSelector is not null)
        {
            return await CreateContextReader(context)
                .ReadPipelineAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _getUri);
        HttpResponseMessage? response = null;
        var responseOwnershipTransferred = false;
        var requestCancellation = CreateRequestCancellation(cancellationToken);
        try
        {
            response = await _httpClient
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellation.Token
                )
                .ConfigureAwait(false);
            var revision = response.Headers.ETag?.ToString();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var missing = ResourceReadResult.NotFound(revision);
                SetLastSnapshot(Snapshot(missing));
                return PipelineResourceReadResult.NotFound(revision);
            }

            if (IsTemporarilyUnavailable(response.StatusCode))
            {
                var unavailable = ResourceReadResult.Unavailable(revision);
                SetLastSnapshot(Snapshot(unavailable));
                return PipelineResourceReadResult.Unavailable(revision);
            }

            response.EnsureSuccessStatusCode();
            var schema = ReadSchemaMetadata(response.Headers);
            var stream = await response
                .Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var owner = new HttpResponseOwner(response, requestCancellation);
            var pipelineResult = PipelineResourceReader.FromStream(
                stream,
                revision,
                schema,
                owner: owner,
                contentFingerprintCompleted: fingerprint =>
                    SetLastSnapshot(HttpResourceSnapshot.Success(revision, fingerprint, schema))
            );
            responseOwnershipTransferred = true;
            return pipelineResult;
        }
#if NETSTANDARD
        catch (HttpRequestException)
#else
        catch (HttpRequestException exception) when (exception.StatusCode is null)
#endif
        {
            var unavailable = ResourceReadResult.Unavailable();
            SetLastSnapshot(Snapshot(unavailable));
            return PipelineResourceReadResult.Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var unavailable = ResourceReadResult.Unavailable();
            SetLastSnapshot(Snapshot(unavailable));
            return PipelineResourceReadResult.Unavailable();
        }
        finally
        {
            if (!responseOwnershipTransferred)
            {
                response?.Dispose();
                requestCancellation.Dispose();
            }
        }
    }

    /// <summary>Creates the optional write capability for this resource.</summary>
    public HttpResourceWriter CreateWriter() => new(this);

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    ) => await ReadAsync(ConfiglueResourceContext.Default, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (_endpointRootSelector is not null)
        {
            return await CreateContextReader(context)
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var response = await SendReadAsync(
                conditional: false,
                observedRevision: null,
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
        SetLastSnapshot(response.ObservedSnapshot);
        return response.Result;
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) =>
        await WaitForChangeAsync(
                ConfiglueResourceContext.Default,
                observedRevision,
                cancellationToken
            )
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        if (_endpointRootSelector is not null)
        {
            await WaitForContextChangeAsync(context, observedRevision, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var baseline = GetLastSnapshot();
        if (baseline is null)
        {
            var initial = await SendReadAsync(
                    conditional: false,
                    observedRevision: null,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            baseline = initial.ObservedSnapshot;
            SetLastSnapshot(baseline.Value);
        }

        if (!string.Equals(observedRevision, baseline.Value.Revision, StringComparison.Ordinal))
        {
            return;
        }

        var waiter = new ChangeWaiter(baseline.Value);
        lock (_watchGate)
        {
            var current = GetLastSnapshot();
            if (
                current is { } currentSnapshot
                && !HasSameRevisionOrContent(baseline.Value, currentSnapshot)
            )
            {
                return;
            }

            _changeWaiters.Add(waiter);
            if (_sharedWatchTask is null)
            {
                StartSharedWatchLoopLocked();
            }
        }

        try
        {
            await waiter.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RemoveChangeWaiter(waiter);
        }
    }

    private void StartSharedWatchLoopLocked()
    {
        var cancellation = new CancellationTokenSource();
        _sharedWatchCancellation = cancellation;
        _sharedWatchTask = Task.Run(
            () => RunSharedWatchLoopAsync(cancellation),
            CancellationToken.None
        );
    }

    private async Task RunSharedWatchLoopAsync(CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            var pollingInterval = _pollingInterval;
            while (true)
            {
                HttpResourceSnapshot baseline;
                lock (_watchGate)
                {
                    if (_changeWaiters.Count == 0)
                    {
                        return;
                    }

                    baseline =
                        GetLastSnapshot()
                        ?? throw new InvalidOperationException(
                            "The HTTP watch loop started without a resource snapshot."
                        );
                    CompleteChangedWaitersLocked(baseline);
                    if (_changeWaiters.Count == 0)
                    {
                        continue;
                    }
                }

                await Task.Delay(pollingInterval, cancellationToken).ConfigureAwait(false);
                var response = await SendReadAsync(
                        conditional: true,
                        observedRevision: baseline.Revision,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);
                if (response.Result.Status == StateReadStatus.Unavailable)
                {
                    pollingInterval =
                        pollingInterval.Ticks >= _maximumPollingInterval.Ticks / 2
                            ? _maximumPollingInterval
                            : TimeSpan.FromTicks(pollingInterval.Ticks * 2);
                    continue;
                }

                pollingInterval = _pollingInterval;
                if (response.NotModified)
                {
                    continue;
                }

                if (!HasSameRevisionOrContent(baseline, response.ObservedSnapshot))
                {
                    SetLastSnapshot(response.ObservedSnapshot);
                    lock (_watchGate)
                    {
                        CompleteChangedWaitersLocked(response.ObservedSnapshot);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                lock (_watchGate)
                {
                    foreach (var waiter in _changeWaiters)
                    {
                        waiter.Completion.TrySetException(exception);
                    }

                    _changeWaiters.Clear();
                }
            }
        }
        finally
        {
            lock (_watchGate)
            {
                if (ReferenceEquals(_sharedWatchCancellation, cancellation))
                {
                    _sharedWatchCancellation = null;
                    _sharedWatchTask = null;
                    if (_changeWaiters.Count > 0)
                    {
                        StartSharedWatchLoopLocked();
                    }
                }
            }

            cancellation.Dispose();
        }
    }

    private void CompleteChangedWaitersLocked(HttpResourceSnapshot snapshot)
    {
        for (var index = _changeWaiters.Count - 1; index >= 0; index--)
        {
            var waiter = _changeWaiters[index];
            if (HasSameRevisionOrContent(waiter.Baseline, snapshot))
            {
                continue;
            }

            _changeWaiters.RemoveAt(index);
            waiter.Completion.TrySetResult();
        }
    }

    private void RemoveChangeWaiter(ChangeWaiter waiter)
    {
        lock (_watchGate)
        {
            _changeWaiters.Remove(waiter);
            if (_changeWaiters.Count == 0)
            {
                _sharedWatchCancellation?.Cancel();
            }
        }
    }

    internal async ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest resourceRequest,
        CancellationToken cancellationToken
    ) =>
        await WriteAsync(ConfiglueResourceContext.Default, resourceRequest, cancellationToken)
            .ConfigureAwait(false);

    internal async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest resourceRequest,
        CancellationToken cancellationToken
    )
    {
        if (_endpointRootSelector is not null)
        {
            return await CreateContextReader(context)
                .WriteAsync(resourceRequest, cancellationToken)
                .ConfigureAwait(false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, _updateUri)
        {
            Content = CreateContent(resourceRequest.Content),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);
        AddSchemaHeaders(request, resourceRequest.Schema);

        var checkRevision = !resourceRequest.Condition.IsNone;
        if (checkRevision)
        {
            if (resourceRequest.Condition.Revision is null)
            {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            }
            else
            {
                request.Headers.IfMatch.Add(
                    ParseStrongEntityTag(resourceRequest.Condition.Revision)
                );
            }
        }

        using var requestCancellation = CreateRequestCancellation(cancellationToken);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            throw new StateConflictException(
                $"The HTTP resource '{_updateUri}' changed after it was read."
            );
        }

        response.EnsureSuccessStatusCode();
        var revision = response.Headers.ETag?.ToString();
        SetLastSnapshot(
            HttpResourceSnapshot.Success(
                revision,
                revision is null ? GetContentFingerprint(resourceRequest.Content.Span) : null,
                resourceRequest.Schema
            )
        );
        return new StateWriteResult(revision);
    }

    private HttpResourceReader CreateContextReader(ConfiglueResourceContext context)
    {
        var endpointRoot = ResolveContextEndpointRoot(context);
        return new HttpResourceReader(
            _httpClient,
            endpointRoot,
            _fixedOptions,
            _configuredResourceId
        );
    }

    private Uri ResolveContextEndpointRoot(ConfiglueResourceContext context)
    {
        var endpointRoot = _endpointRootSelector!(context);
        ArgumentNullException.ThrowIfNull(endpointRoot);
        return EnsureTrailingSlash(endpointRoot);
    }

    private async ValueTask WaitForContextChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        var endpointRoot = ResolveContextEndpointRoot(context);
        var endpointKey = endpointRoot.AbsoluteUri;
        ContextWatcher watcher;
        while (true)
        {
            watcher = _contextWatchers.GetOrAdd(
                endpointKey,
                _ => new ContextWatcher(
                    new HttpResourceReader(
                        _httpClient,
                        endpointRoot,
                        _fixedOptions,
                        _configuredResourceId
                    )
                )
            );
            if (watcher.TryEnter())
            {
                break;
            }

            await watcher.Retired.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await watcher
                .Reader.WaitForChangeAsync(observedRevision, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (watcher.Leave())
            {
                try
                {
                    await watcher.Reader.StopWatchLoopIfIdleAsync().ConfigureAwait(false);
                }
                finally
                {
                    _contextWatchers.TryRemove(endpointKey, out _);
                    watcher.CompleteRetirement();
                }
            }
        }
    }

    private async ValueTask StopWatchLoopIfIdleAsync()
    {
        Task? watchTask;
        lock (_watchGate)
        {
            if (_changeWaiters.Count > 0)
            {
                return;
            }

            _sharedWatchCancellation?.Cancel();
            watchTask = _sharedWatchTask;
        }

        if (watchTask is not null)
        {
            await watchTask.ConfigureAwait(false);
        }
    }

    private async ValueTask<HttpReadResponse> SendReadAsync(
        bool conditional,
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _getUri);
        if (conditional && observedRevision is not null)
        {
            if (!EntityTagHeaderValue.TryParse(observedRevision, out var observedTag))
            {
                throw new ArgumentException(
                    "The observed HTTP revision must be a valid ETag.",
                    nameof(observedRevision)
                );
            }

            request.Headers.IfNoneMatch.Add(observedTag);
        }

        using var requestCancellation = CreateRequestCancellation(cancellationToken);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellation.Token
                )
                .ConfigureAwait(false);
        }
#if NETSTANDARD
        catch (HttpRequestException)
#else
        catch (HttpRequestException exception) when (exception.StatusCode is null)
#endif
        {
            var unavailable = ResourceReadResult.Unavailable();
            return new HttpReadResponse(unavailable, Snapshot(unavailable), NotModified: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var unavailable = ResourceReadResult.Unavailable();
            return new HttpReadResponse(unavailable, Snapshot(unavailable), NotModified: false);
        }

        using (response)
        {
            if (conditional && response.StatusCode == HttpStatusCode.NotModified)
            {
                return HttpReadResponse.Unchanged;
            }

            var revision = response.Headers.ETag?.ToString();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var missing = ResourceReadResult.NotFound(revision);
                return new HttpReadResponse(missing, Snapshot(missing), NotModified: false);
            }

            if (IsTemporarilyUnavailable(response.StatusCode))
            {
                var unavailable = ResourceReadResult.Unavailable(revision);
                return new HttpReadResponse(unavailable, Snapshot(unavailable), NotModified: false);
            }

            response.EnsureSuccessStatusCode();
            var content = await response
                .Content.ReadAsByteArrayAsync(requestCancellation.Token)
                .ConfigureAwait(false);
            var schema = ReadSchemaMetadata(response.Headers);
            var result = ResourceReadResult.Success(content, revision, schema);
            return new HttpReadResponse(result, Snapshot(result), NotModified: false);
        }
    }

    private static bool IsTemporarilyUnavailable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout
        || (int)statusCode == 429
        || (int)statusCode is >= 500 and <= 599;

    private CancellationTokenSource CreateRequestCancellation(CancellationToken cancellationToken)
    {
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        requestCancellation.CancelAfter(_requestTimeout);
        return requestCancellation;
    }

    private static StateSchemaMetadata? ReadSchemaMetadata(HttpResponseHeaders headers)
    {
        var modelId = ReadSingleHeader(headers, SchemaIdHeaderName);
        var versionValue = ReadSingleHeader(headers, SchemaVersionHeaderName);
        if (modelId is null && versionValue is null)
        {
            return null;
        }

        if (
            versionValue is null
            || !int.TryParse(
                versionValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var version
            )
            || version < StateSchemaMetadata.InitialVersion
        )
        {
            throw new FormatException(
                $"The HTTP resource returned an invalid {SchemaVersionHeaderName} header."
            );
        }

        return new StateSchemaMetadata(modelId, version);
    }

    private static string? ReadSingleHeader(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
        {
            return null;
        }

        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            throw new FormatException($"The HTTP resource returned an invalid {name} header.");
        }

        var value = enumerator.Current;
        if (string.IsNullOrWhiteSpace(value) || enumerator.MoveNext())
        {
            throw new FormatException($"The HTTP resource returned an invalid {name} header.");
        }

        return value;
    }

    private static void AddSchemaHeaders(HttpRequestMessage request, StateSchemaMetadata? schema)
    {
        if (schema is not { } metadata)
        {
            return;
        }

        if (metadata.ModelId is not null)
        {
            request.Headers.TryAddWithoutValidation(SchemaIdHeaderName, metadata.ModelId);
        }

        request.Headers.TryAddWithoutValidation(
            SchemaVersionHeaderName,
            metadata.Version.ToString(CultureInfo.InvariantCulture)
        );
    }

    private static EntityTagHeaderValue ParseStrongEntityTag(string revision)
    {
        if (!EntityTagHeaderValue.TryParse(revision, out var entityTag) || entityTag.IsWeak)
        {
            throw new ArgumentException(
                "Conditional HTTP writes require a strong ETag revision.",
                nameof(revision)
            );
        }

        return entityTag;
    }

    private static HttpResourceSnapshot Snapshot(ResourceReadResult result) =>
        new(
            result.Status,
            result.Revision,
            result.Status == StateReadStatus.Success && result.Revision is null
                ? GetContentFingerprint(result.Content.Span)
                : null,
            result.Schema
        );

    private static bool HasSameRevisionOrContent(
        HttpResourceSnapshot baseline,
        HttpResourceSnapshot current
    )
    {
        if (baseline.Status != current.Status || baseline.Schema != current.Schema)
        {
            return false;
        }

        if (baseline.Revision is not null || current.Revision is not null)
        {
            return string.Equals(baseline.Revision, current.Revision, StringComparison.Ordinal);
        }

        return string.Equals(
            baseline.ContentFingerprint,
            current.ContentFingerprint,
            StringComparison.Ordinal
        );
    }

    private static string GetContentFingerprint(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));

    private static HttpContent CreateContent(ReadOnlyMemory<byte> content)
    {
        if (
            MemoryMarshal.TryGetArray(content, out var segment)
            && segment.Array is byte[] array
            && segment.Offset == 0
            && segment.Count == array.Length
        )
        {
            return new ByteArrayContent(array);
        }

        return new MemoryContent(content);
    }

    private static Uri EnsureTrailingSlash(Uri endpointRoot)
    {
        var builder = new UriBuilder(endpointRoot);
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static Uri CombineEndpoint(Uri endpointRoot, string path, string optionName)
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('?')
            || path.Contains('#')
            || !Uri.TryCreate(path, UriKind.Relative, out var relativePath)
            || relativePath.IsAbsoluteUri
        )
        {
            throw new ArgumentException(
                "HTTP resource endpoint paths must be non-empty relative paths without a query or fragment.",
                optionName
            );
        }

        var endpoint = new Uri(endpointRoot, relativePath);
        if (!endpointRoot.IsBaseOf(endpoint))
        {
            throw new ArgumentException(
                "HTTP resource endpoint paths must remain under the endpoint root.",
                optionName
            );
        }

        return endpoint;
    }

    private static ResourceId CreateResourceId(Uri root, Uri getUri, Uri updateUri)
    {
        var identity = Encoding.UTF8.GetBytes(
            root.AbsoluteUri + "\n" + getUri.AbsoluteUri + "\n" + updateUri.AbsoluteUri
        );
        return new ResourceId(
            "http:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(identity))
        );
    }

    private HttpResourceSnapshot? GetLastSnapshot()
    {
        lock (_snapshotGate)
        {
            return _lastSnapshot;
        }
    }

    private void SetLastSnapshot(HttpResourceSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            _lastSnapshot = snapshot;
        }
    }

    private readonly record struct HttpResourceSnapshot
    {
        public StateReadStatus Status { get; init; }
        public string? Revision { get; init; }
        public string? ContentFingerprint { get; init; }
        public StateSchemaMetadata? Schema { get; init; }

        public HttpResourceSnapshot(
            StateReadStatus Status,
            string? Revision,
            string? ContentFingerprint,
            StateSchemaMetadata? Schema
        )
        {
            this.Status = Status;
            this.Revision = Revision;
            this.ContentFingerprint = ContentFingerprint;
            this.Schema = Schema;
        }

        public void Deconstruct(
            out StateReadStatus Status,
            out string? Revision,
            out string? ContentFingerprint,
            out StateSchemaMetadata? Schema
        )
        {
            Status = this.Status;
            Revision = this.Revision;
            ContentFingerprint = this.ContentFingerprint;
            Schema = this.Schema;
        }

        public static HttpResourceSnapshot Success(
            string? revision,
            string? fingerprint,
            StateSchemaMetadata? schema
        ) => new(StateReadStatus.Success, revision, fingerprint, schema);
    }

    private readonly record struct HttpReadResponse
    {
        public ResourceReadResult Result { get; init; }
        public HttpResourceSnapshot ObservedSnapshot { get; init; }
        public bool NotModified { get; init; }

        public HttpReadResponse(
            ResourceReadResult Result,
            HttpResourceSnapshot ObservedSnapshot,
            bool NotModified
        )
        {
            this.Result = Result;
            this.ObservedSnapshot = ObservedSnapshot;
            this.NotModified = NotModified;
        }

        public void Deconstruct(
            out ResourceReadResult Result,
            out HttpResourceSnapshot ObservedSnapshot,
            out bool NotModified
        )
        {
            Result = this.Result;
            ObservedSnapshot = this.ObservedSnapshot;
            NotModified = this.NotModified;
        }

        public static HttpReadResponse Unchanged { get; } =
            new(default, default, NotModified: true);
    }

    private sealed class ChangeWaiter(HttpResourceSnapshot baseline)
    {
        public HttpResourceSnapshot Baseline { get; } = baseline;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ContextWatcher(HttpResourceReader reader)
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _retired = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _waiterCount;
        private bool _retiring;

        public HttpResourceReader Reader { get; } = reader;

        public Task Retired => _retired.Task;

        public bool TryEnter()
        {
            lock (_gate)
            {
                if (_retiring)
                {
                    return false;
                }

                _waiterCount++;
                return true;
            }
        }

        public bool Leave()
        {
            lock (_gate)
            {
                _waiterCount--;
                if (_waiterCount != 0)
                {
                    return false;
                }

                _retiring = true;
                return true;
            }
        }

        public void CompleteRetirement() => _retired.TrySetResult();
    }

    private sealed class HttpResponseOwner(
        HttpResponseMessage response,
        CancellationTokenSource requestCancellation
    ) : IDisposable
    {
        public void Dispose()
        {
            response.Dispose();
            requestCancellation.Dispose();
        }
    }

    private sealed class MemoryContent : HttpContent
    {
        private readonly ReadOnlyMemory<byte> _content;

        public MemoryContent(ReadOnlyMemory<byte> content) => _content = content;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_content).AsTask();

#if !NETSTANDARD
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken
        ) => stream.WriteAsync(_content, cancellationToken).AsTask();
#endif

        protected override bool TryComputeLength(out long length)
        {
            length = _content.Length;
            return true;
        }
    }
}
