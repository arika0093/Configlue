using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;

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
/// </remarks>
public sealed class HttpStateReader<TFragment>
    : ISourceCapabilities<TFragment>,
        ISourceWriter<TFragment>,
        ISourceWatcher,
        IResourceIdentity
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly Uri _eventsEndpoint;
    private readonly JsonSerializerOptions? _serializerOptions;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _reconnectInitialDelay;
    private readonly TimeSpan _reconnectMaxDelay;
    private readonly bool _writable;
    private readonly bool _watchEnabled;
    private readonly ResourceId _resourceId;
    private readonly object _snapshotGate = new();
    private readonly object _watchGate = new();
    private readonly List<ChangeWaiter> _changeWaiters = [];
    private CancellationTokenSource? _sharedWatchCancellation;
    private Task? _sharedWatchTask;
    private string? _lastRevision;

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

        _httpClient = httpClient;
        _endpoint = endpoint;
        _eventsEndpoint = eventsEndpoint;
        _serializerOptions = serializerOptions;
        _requestTimeout = timeout;
        _reconnectInitialDelay = initial;
        _reconnectMaxDelay = max;
        _writable = writable;
        _watchEnabled = watchEnabled;
        _resourceId = CreateResourceId(endpoint, eventsEndpoint);
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
        var response = await SendGetAsync(null, cancellationToken).ConfigureAwait(false);
        SetLastRevision(response.Revision);
        return response.Result;
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_writable)
        {
            throw new InvalidOperationException("This State HTTP source is read-only.");
        }

        byte[] body;
        try
        {
            var converter = ConfiglueJsonFragmentRegistry<TFragment>.Converter;
            var options = ConfiglueFragmentJson.CreateOptions(_serializerOptions);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
#pragma warning disable S6966 // Utf8JsonWriter over IBufferWriter only offers synchronous Flush.
                converter.Write(writer, request.Value, options);
                writer.Flush();
#pragma warning restore S6966
            }

            body = buffer.WrittenMemory.ToArray();
        }
        catch (Exception exception)
        {
            throw new HttpStateRequestException(
                "Failed to serialize the state fragment: " + exception.Message
            );
        }

        using var message = new HttpRequestMessage(HttpMethod.Put, _endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (request.Condition.IsMatch)
        {
            message.Headers.IfMatch.Add(
                new EntityTagHeaderValue($"\"{request.Condition.Revision}\"")
            );
        }
        else if (request.Condition.IsMustNotExist)
        {
            message.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        }

        using var timeout = CreateRequestCancellation(cancellationToken);
        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
#if NETSTANDARD
        catch (HttpRequestException exception)
#else
        catch (HttpRequestException exception) when (exception.StatusCode is null)
#endif
        {
            throw new HttpStateUnavailableException(
                "The State HTTP endpoint is unavailable.",
                exception
            );
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpStateUnavailableException("The State HTTP request timed out.", exception);
        }

        using (httpResponse)
        {
            var revision = ParseEtagToRevision(httpResponse.Headers.ETag?.ToString());
            if (
                httpResponse.StatusCode == HttpStatusCode.OK
                || httpResponse.StatusCode == HttpStatusCode.NoContent
            )
            {
                SetLastRevision(revision);
                return new StateWriteResult(revision);
            }

            var detail = await ReadProblemDetailAsync(httpResponse, timeout.Token)
                .ConfigureAwait(false);
            switch (httpResponse.StatusCode)
            {
                case HttpStatusCode.BadRequest:
                    throw new HttpStateRequestException(
                        "The server rejected the state payload: " + detail
                    );
                case (HttpStatusCode)422:
                    throw new HttpStateValidationException(
                        "The server rejected the state value: " + detail,
                        SplitFailures(detail)
                    );
                case HttpStatusCode.PreconditionFailed:
                    throw new HttpStateStaleException(
                        "The effective state changed after it was read (stale If-Match)."
                    );
                case HttpStatusCode.Conflict:
                    throw new HttpStateWriteConflictException(
                        "The state write conflicts with a concurrent change: " + detail
                    );
                case HttpStatusCode.Unauthorized:
                    throw new HttpStateUnauthorizedException(
                        "The State HTTP endpoint requires authentication."
                    );
                case HttpStatusCode.Forbidden:
                    throw new HttpStateForbiddenException(
                        "The State HTTP endpoint denied the request."
                    );
                default:
                    if (IsTemporarilyUnavailable(httpResponse.StatusCode))
                    {
                        throw new HttpStateUnavailableException(
                            $"The State HTTP endpoint returned {(int)httpResponse.StatusCode}: {detail}"
                        );
                    }

                    httpResponse.EnsureSuccessStatusCode();
                    throw new HttpStateException(
                        $"Unexpected State HTTP status {(int)httpResponse.StatusCode}: {detail}"
                    );
            }
        }
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

        cancellationToken.ThrowIfCancellationRequested();
        var baseline = GetLastRevision();
        if (baseline is null)
        {
            var initial = await SendGetAsync(null, cancellationToken).ConfigureAwait(false);
            baseline = initial.Revision;
            SetLastRevision(baseline);
        }

        if (!string.Equals(observedRevision, baseline, StringComparison.Ordinal))
        {
            return;
        }

        var waiter = new ChangeWaiter(baseline);
        lock (_watchGate)
        {
            var current = GetLastRevision();
            if (!string.Equals(baseline, current, StringComparison.Ordinal))
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
        var backoff = _reconnectInitialDelay;
        try
        {
            while (true)
            {
                string? baseline;
                lock (_watchGate)
                {
                    if (_changeWaiters.Count == 0)
                    {
                        return;
                    }

                    baseline = GetLastRevision();
                    CompleteChangedWaitersLocked(baseline);
                    if (_changeWaiters.Count == 0)
                    {
                        continue;
                    }
                }

                string? eventRevision;
                try
                {
                    // Convergence check before opening SSE: closes the race where the server
                    // changes between the watcher's baseline read and the SSE subscription.
                    var preCheck = await SendGetAsync(null, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(baseline, preCheck.Revision, StringComparison.Ordinal))
                    {
                        SetLastRevision(preCheck.Revision);
                        lock (_watchGate)
                        {
                            CompleteChangedWaitersLocked(preCheck.Revision);
                        }

                        backoff = _reconnectInitialDelay;
                        continue;
                    }

                    eventRevision = await WaitForSseInvalidationAsync(baseline, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // Transport failure: converge via re-read, then back off before reopening SSE.
                    try
                    {
                        var converged = await SendGetAsync(null, cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.Equals(baseline, converged.Revision, StringComparison.Ordinal))
                        {
                            SetLastRevision(converged.Revision);
                            lock (_watchGate)
                            {
                                CompleteChangedWaitersLocked(converged.Revision);
                            }

                            backoff = _reconnectInitialDelay;
                            continue;
                        }
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        // Convergence re-reads failed; fall through to backoff and SSE reopen.
                        // The shared watch loop must survive transient transport failures.
                        System.Diagnostics.Debug.WriteLine(exception);
                    }

                    try
                    {
                        await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    backoff = NextBackoff(backoff);
                    continue;
                }

                // SSE signaled a change: re-read to converge even if events were missed.
                backoff = _reconnectInitialDelay;
                HttpGetResult convergedResult;
                try
                {
                    convergedResult = await SendGetAsync(null, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    try
                    {
                        await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    backoff = NextBackoff(backoff);
                    continue;
                }

                var newRevision = convergedResult.Revision ?? eventRevision;
                if (!string.Equals(baseline, newRevision, StringComparison.Ordinal))
                {
                    SetLastRevision(newRevision);
                    lock (_watchGate)
                    {
                        CompleteChangedWaitersLocked(newRevision);
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

    private async ValueTask<string?> WaitForSseInvalidationAsync(
        string? baselineRevision,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _eventsEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (baselineRevision is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", baselineRevision);
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new HttpStateUnauthorizedException(
                "The State HTTP events endpoint requires authentication."
            );
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HttpStateForbiddenException(
                "The State HTTP events endpoint denied the request."
            );
        }

        response.EnsureSuccessStatusCode();
#if NETSTANDARD2_0
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#else
        using var stream = await response
            .Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
#endif
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventType = null;
        string? eventId = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                throw new IOException("The State HTTP events stream closed.");
            }

            if (line.Length == 0)
            {
                if (string.Equals(eventType, "changed", StringComparison.Ordinal))
                {
                    return eventId;
                }

                eventType = null;
                eventId = null;
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var field = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).TrimStart();
            if (string.Equals(field, "event", StringComparison.Ordinal))
            {
                eventType = value;
            }
            else if (string.Equals(field, "id", StringComparison.Ordinal))
            {
                eventId = value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException(cancellationToken);
    }

    private void CompleteChangedWaitersLocked(string? currentRevision)
    {
        for (var index = _changeWaiters.Count - 1; index >= 0; index--)
        {
            var waiter = _changeWaiters[index];
            if (string.Equals(waiter.Baseline, currentRevision, StringComparison.Ordinal))
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

    private async ValueTask<HttpGetResult> SendGetAsync(
        string? ifNoneMatchRevision,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (ifNoneMatchRevision is not null)
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{ifNoneMatchRevision}\""));
        }

        using var timeout = CreateRequestCancellation(cancellationToken);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
#if NETSTANDARD
        catch (HttpRequestException)
#else
        catch (HttpRequestException exception) when (exception.StatusCode is null)
#endif
        {
            return new HttpGetResult(StateReadResult<TFragment>.Unavailable(), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HttpGetResult(StateReadResult<TFragment>.Unavailable(), null);
        }

        using (response)
        {
            var revision = ParseEtagToRevision(response.Headers.ETag?.ToString());
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new HttpGetResult(StateReadResult<TFragment>.NotFound(revision), revision);
            }

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new HttpGetResult(
                    StateReadResult<TFragment>.Unavailable(revision),
                    revision
                );
            }

            if (
                response.StatusCode == (HttpStatusCode)422
                || response.StatusCode == HttpStatusCode.BadRequest
            )
            {
                return new HttpGetResult(
                    StateReadResult<TFragment>.InvalidPayload(default, revision),
                    revision
                );
            }

            if (IsTemporarilyUnavailable(response.StatusCode))
            {
                return new HttpGetResult(
                    StateReadResult<TFragment>.Unavailable(revision),
                    revision
                );
            }

            if (
                response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden
            )
            {
                response.EnsureSuccessStatusCode();
            }

            response.EnsureSuccessStatusCode();
            byte[] content;
            try
            {
#if NETSTANDARD2_0
                content = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#else
                content = await response
                    .Content.ReadAsByteArrayAsync(timeout.Token)
                    .ConfigureAwait(false);
#endif
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new HttpGetResult(
                    StateReadResult<TFragment>.Unavailable(revision),
                    revision
                );
            }

            TFragment fragment;
            try
            {
                var converter = ConfiglueJsonFragmentRegistry<TFragment>.Converter;
                var options = ConfiglueFragmentJson.CreateOptions(_serializerOptions);
                var jsonReader = new Utf8JsonReader(content);
                if (!jsonReader.Read())
                {
                    throw new JsonException("The State HTTP payload is empty.");
                }

                fragment = converter.Read(ref jsonReader, typeof(TFragment), options)!;
            }
            catch (JsonException)
            {
                return new HttpGetResult(
                    StateReadResult<TFragment>.InvalidPayload(default, revision),
                    revision
                );
            }

            if (fragment is null)
            {
                return new HttpGetResult(
                    StateReadResult<TFragment>.InvalidPayload(default, revision),
                    revision
                );
            }

            return new HttpGetResult(
                StateReadResult<TFragment>.Success(fragment, revision),
                revision
            );
        }
    }

    private CancellationTokenSource CreateRequestCancellation(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_requestTimeout);
        return linked;
    }

    private TimeSpan NextBackoff(TimeSpan current)
    {
        var doubled = TimeSpan.FromTicks(Math.Min(current.Ticks * 2, _reconnectMaxDelay.Ticks));
        return doubled;
    }

    private string? GetLastRevision()
    {
        lock (_snapshotGate)
        {
            return _lastRevision;
        }
    }

    private void SetLastRevision(string? revision)
    {
        lock (_snapshotGate)
        {
            _lastRevision = revision;
        }
    }

    private static bool IsTemporarilyUnavailable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout
        || (int)statusCode == 429
        || (int)statusCode is >= 500 and <= 599;

    private static string? ParseEtagToRevision(string? etag)
    {
        if (etag is null || string.IsNullOrWhiteSpace(etag))
        {
            return null;
        }

        var trimmed = etag.Trim();
        if (trimmed.StartsWith("W/", StringComparison.Ordinal))
        {
            return null;
        }

        if (trimmed.Length < 2 || !trimmed.StartsWith('"') || !trimmed.EndsWith('"'))
        {
            return null;
        }

        var inner = trimmed[1..^1];
        if (inner.Length != 64)
        {
            return null;
        }

        if (inner.Any(static c => !Uri.IsHexDigit(c)))
        {
            return null;
        }

        return inner.ToLowerInvariant();
    }

    private static ResourceId CreateResourceId(Uri endpoint, Uri eventsEndpoint)
    {
        var identity = Encoding.UTF8.GetBytes(
            endpoint.AbsoluteUri + "\n" + eventsEndpoint.AbsoluteUri
        );
        return new ResourceId(
            "http-state:"
                + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(identity))
        );
    }

    private static async ValueTask<string> ReadProblemDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        try
        {
#if NETSTANDARD2_0
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#else
            var body = await response
                .Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
#endif
            if (string.IsNullOrWhiteSpace(body))
            {
                return response.ReasonPhrase ?? response.StatusCode.ToString();
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (
                    document.RootElement.TryGetProperty("detail", out var detail)
                    && detail.ValueKind == JsonValueKind.String
                )
                {
                    return detail.GetString() ?? body;
                }
            }
            catch (JsonException exception)
            {
                // Non-JSON error bodies fall through to the truncated raw body below.
                System.Diagnostics.Debug.WriteLine(exception);
            }

            return body.Length > 500 ? body.Substring(0, 500) : body;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return response.ReasonPhrase ?? response.StatusCode.ToString();
        }
    }

    private static IReadOnlyList<string> SplitFailures(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return [];
        }

        return detail
            .Split(["; "], StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Trim())
            .Where(static part => part.Length > 0)
            .ToArray();
    }

    private readonly record struct HttpGetResult(
        StateReadResult<TFragment> Result,
        string? Revision
    );

    private sealed class ChangeWaiter(string? baseline)
    {
        public string? Baseline { get; } = baseline;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
