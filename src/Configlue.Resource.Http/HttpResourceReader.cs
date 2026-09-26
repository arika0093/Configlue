using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Configlue.Resource.Http;

/// <summary>Reads and watches a byte resource exposed through the Configlue HTTP resource protocol.</summary>
public sealed class HttpResourceReader : IResourceReader, IStateWatcher, IResourceIdentity
{
    /// <summary>Response and request header carrying the source schema identifier.</summary>
    public const string SchemaIdHeaderName = "Configlue-Schema-Id";

    /// <summary>Response and request header carrying the source schema version.</summary>
    public const string SchemaVersionHeaderName = "Configlue-Schema-Version";

    private readonly HttpClient _httpClient;
    private readonly Uri _getUri;
    private readonly Uri _updateUri;
    private readonly string _contentType;
    private readonly TimeSpan _pollingInterval;
    private readonly object _snapshotGate = new();
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

        if (!MediaTypeHeaderValue.TryParse(configuredOptions.ContentType, out var contentType))
        {
            throw new ArgumentException("ContentType must be a valid media type.", nameof(options));
        }

        _contentType = contentType.ToString();
        _pollingInterval = configuredOptions.PollingInterval;
        ResourceId = resourceId ?? CreateResourceId(root, _getUri, _updateUri);
    }

    /// <inheritdoc />
    public ResourceId ResourceId { get; }

    /// <summary>The GET endpoint used to read the resource.</summary>
    public Uri GetUri => _getUri;

    /// <summary>Creates the optional write capability for this resource.</summary>
    public HttpResourceWriter CreateWriter() => new(this);

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
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
    )
    {
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

        while (true)
        {
            await Task.Delay(_pollingInterval, cancellationToken).ConfigureAwait(false);
            var response = await SendReadAsync(
                    conditional: true,
                    observedRevision: observedRevision,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            if (response.NotModified)
            {
                continue;
            }

            if (!HasSameRevisionOrContent(baseline.Value, response.ObservedSnapshot))
            {
                SetLastSnapshot(response.ObservedSnapshot);
                return;
            }
        }
    }

    internal async ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest resourceRequest,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, _updateUri)
        {
            Content = new ByteArrayContent(resourceRequest.Content.ToArray()),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);
        AddSchemaHeaders(request, resourceRequest.Schema);

        var checkRevision =
            resourceRequest.CheckRevision || resourceRequest.ExpectedRevision is not null;
        if (checkRevision)
        {
            if (resourceRequest.ExpectedRevision is null)
            {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            }
            else
            {
                request.Headers.IfMatch.Add(ParseStrongEntityTag(resourceRequest.ExpectedRevision));
            }
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
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
                GetContentFingerprint(resourceRequest.Content.Span),
                resourceRequest.Schema
            )
        );
        return new StateWriteResult(revision);
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

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
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
                .Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var schema = ReadSchemaMetadata(response.Headers);
            var result = ResourceReadResult.Success(content, revision, schema);
            return new HttpReadResponse(result, Snapshot(result), NotModified: false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null)
        {
            var unavailable = ResourceReadResult.Unavailable();
            return new HttpReadResponse(unavailable, Snapshot(unavailable), NotModified: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var unavailable = ResourceReadResult.Unavailable();
            return new HttpReadResponse(unavailable, Snapshot(unavailable), NotModified: false);
        }
    }

    private static bool IsTemporarilyUnavailable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)statusCode is >= 500 and <= 599;

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

        var materialized = values.ToArray();
        if (materialized.Length != 1 || string.IsNullOrWhiteSpace(materialized[0]))
        {
            throw new FormatException($"The HTTP resource returned an invalid {name} header.");
        }

        return materialized[0];
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
            result.Status == StateReadStatus.Success
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
        Convert.ToHexString(SHA256.HashData(content));

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
        return new ResourceId("http:" + Convert.ToHexString(SHA256.HashData(identity)));
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

    private readonly record struct HttpResourceSnapshot(
        StateReadStatus Status,
        string? Revision,
        string? ContentFingerprint,
        StateSchemaMetadata? Schema
    )
    {
        public static HttpResourceSnapshot Success(
            string? revision,
            string fingerprint,
            StateSchemaMetadata? schema
        ) => new(StateReadStatus.Success, revision, fingerprint, schema);
    }

    private readonly record struct HttpReadResponse(
        ResourceReadResult Result,
        HttpResourceSnapshot ObservedSnapshot,
        bool NotModified
    )
    {
        public static HttpReadResponse Unchanged { get; } =
            new(default, default, NotModified: true);
    }
}
