using System.Text.Json;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using SparseFragments.JsonPatch;

namespace Configlue.Source.Http;

/// <summary>
/// Typed State HTTP reader, writer, and SSE watcher for one generated fragment type.
/// </summary>
/// <remarks>
/// <para>GET status mapping:</para>
/// <list type="table">
/// <listheader><term>HTTP status</term><term>Source status</term></listheader>
/// <item><term>200 OK</term><term>Success (ETag carries the state revision)</term></item>
/// <item><term>404 Not Found</term><term>NotFound</term></item>
/// <item><term>400 / 422</term><term>InvalidPayload</term></item>
/// <item><term>408 / 429 / 5xx, transport or timeout failure</term><term>Unavailable</term></item>
/// </list>
/// <para>401/403 and unexpected PUT failures throw typed <see cref="HttpStateException"/> errors.</para>
/// <para>
/// Implementation is decomposed: <see cref="HttpStateTransport{TFragment}"/> owns
/// GET/PUT/PATCH wire mapping, <see cref="HttpStateWriteStrategy{TFragment}"/> owns
/// PATCH-vs-PUT decisions over <see cref="HttpStateBaselineCache"/>, and
/// <see cref="HttpStateWatchLoop{TFragment}"/> owns SSE reconnect/backoff fan-out.
/// </para>
/// </remarks>
public sealed class HttpStateReader<TFragment>
    : ISourceCapabilities<TFragment>,
        ISourceWriter<TFragment>,
        ISourceWatcher,
        IResourceIdentity
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly Uri _endpoint;
    private readonly Uri _eventsEndpoint;
    private readonly bool _writable;
    private readonly bool _watchEnabled;
    private readonly ResourceId _resourceId;
    private readonly HttpStateTransport<TFragment> _transport;
    private readonly HttpStateBaselineCache _baseline = new();
    private readonly HttpStateWriteStrategy<TFragment> _writeStrategy;
    private readonly HttpStateWatchLoop<TFragment> _watchLoop;

    /// <summary>Creates a State HTTP reader for the supplied endpoint.</summary>
    public HttpStateReader(
        HttpClient httpClient,
        Uri endpoint,
        Uri eventsEndpoint,
        JsonSerializerOptions? serializerOptions = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? reconnectInitialDelay = null,
        TimeSpan? reconnectMaxDelay = null,
        bool writable = false,
        bool watchEnabled = true
    )
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(eventsEndpoint);
        if (
            !endpoint.IsAbsoluteUri
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new ArgumentException(
                "The endpoint must be an absolute HTTP or HTTPS URI.",
                nameof(endpoint)
            );
        }

        if (
            !eventsEndpoint.IsAbsoluteUri
            || (
                eventsEndpoint.Scheme != Uri.UriSchemeHttp
                && eventsEndpoint.Scheme != Uri.UriSchemeHttps
            )
        )
        {
            throw new ArgumentException(
                "The events endpoint must be an absolute HTTP or HTTPS URI.",
                nameof(eventsEndpoint)
            );
        }

        var timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "RequestTimeout must be greater than zero."
            );
        }

        var initial = reconnectInitialDelay ?? TimeSpan.FromMilliseconds(500);
        var max = reconnectMaxDelay ?? TimeSpan.FromSeconds(30);
        if (initial <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reconnectInitialDelay));
        }

        if (max < initial)
        {
            throw new ArgumentOutOfRangeException(nameof(reconnectMaxDelay));
        }

        _endpoint = endpoint;
        _eventsEndpoint = eventsEndpoint;
        _writable = writable;
        _watchEnabled = watchEnabled;
        _resourceId = HttpStateProtocol.CreateResourceId(endpoint, eventsEndpoint);
        _transport = new HttpStateTransport<TFragment>(
            httpClient,
            endpoint,
            serializerOptions,
            timeout
        );
        _writeStrategy = new HttpStateWriteStrategy<TFragment>(
            _transport,
            _baseline,
            serializerOptions,
            writable
        );
        _watchLoop = new HttpStateWatchLoop<TFragment>(
            _transport,
            new HttpSseClient(httpClient, eventsEndpoint),
            _baseline,
            initial,
            max
        );
    }

    /// <inheritdoc />
    public ISourceWriter<TFragment>? Writer => _writable ? this : null;

    /// <inheritdoc />
    public ISourceWatcher? Watcher => _watchEnabled ? this : null;

    /// <summary>The state GET/PUT endpoint.</summary>
    public Uri Endpoint => _endpoint;

    /// <summary>The SSE invalidation endpoint.</summary>
    public Uri EventsEndpoint => _eventsEndpoint;

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) => _resourceId;

    /// <inheritdoc />
    public async ValueTask<StateReadResult<TFragment>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var response = await _transport.GetAsync(null, cancellationToken).ConfigureAwait(false);
        _baseline.CacheGetResult(response.Revision, response.Content);
        return response.Result;
    }

    /// <summary>
    /// Applies an RFC 6902 JSON Patch document to the State HTTP endpoint.
    /// </summary>
    /// <param name="patch">The patch document, using the <c>#201</c> JSON Patch type.</param>
    /// <param name="etag">The baseline effective-state ETag (quoted or hex).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The canonical state observed after the patch and its new revision.</returns>
    /// <exception cref="HttpStateStaleException">The baseline ETag is stale (HTTP 412).</exception>
    /// <exception cref="HttpStateWriteConflictException">
    /// The patch cannot be applied or conflicts with a concurrent change (HTTP 409).
    /// </exception>
    /// <exception cref="HttpStateValidationException">The server rejected the value (HTTP 422).</exception>
    /// <exception cref="HttpStateRequestException">The patch is malformed (HTTP 400).</exception>
    public ValueTask<HttpStatePatchResult<TFragment>> PatchAsync(
        JsonPatchDocument patch,
        string etag,
        CancellationToken cancellationToken = default
    )
    {
        return _writeStrategy.PatchAsync(patch, etag, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken = default
    )
    {
        return _writeStrategy.WriteAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        if (!_watchEnabled)
        {
            throw new InvalidOperationException(
                "This State HTTP source does not support watching."
            );
        }

        await _watchLoop
            .WaitForChangeAsync(observedRevision, cancellationToken)
            .ConfigureAwait(false);
    }
}
