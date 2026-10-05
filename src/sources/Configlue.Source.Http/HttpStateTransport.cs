using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.State;

namespace Configlue.Source.Http;

/// <summary>GET/PUT/PATCH transport for one fragment type with shared timeout/error handling.</summary>
internal sealed class HttpStateTransport<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly JsonSerializerOptions? _serializerOptions;
    private readonly TimeSpan _requestTimeout;

    public HttpStateTransport(
        HttpClient httpClient,
        Uri endpoint,
        JsonSerializerOptions? serializerOptions,
        TimeSpan requestTimeout
    )
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _serializerOptions = serializerOptions;
        _requestTimeout = requestTimeout;
    }

    public async ValueTask<HttpTransportGetResult<TFragment>> GetAsync(
        string? ifNoneMatchRevision,
        CancellationToken cancellationToken
    )
    {
        // Single timeout scope shared by the send and content-read phases, matching
        // the pre-split reader: wall clock is bounded by one requestTimeout, not two.
        using var timeout = HttpStateProtocol.CreateRequestCancellation(
            _requestTimeout,
            cancellationToken
        );
        HttpResponseMessage response;
        try
        {
            response = await HttpStateProtocol
                .SendWithSharedTimeoutAsync(
                    _httpClient,
                    () =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
                        request.Headers.Accept.Add(
                            new MediaTypeWithQualityHeaderValue("application/json")
                        );
                        if (ifNoneMatchRevision is not null)
                        {
                            request.Headers.IfNoneMatch.Add(
                                new EntityTagHeaderValue($"\"{ifNoneMatchRevision}\"")
                            );
                        }

                        return request;
                    },
                    timeout.Token,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (HttpStateUnavailableException)
        {
            // GET maps transport/timeout failures to Unavailable rather than throwing.
            return new HttpTransportGetResult<TFragment>(
                StateReadResult<TFragment>.Unavailable(),
                null
            );
        }

        using (response)
        {
            var revision = HttpStateProtocol.ParseEtagToRevision(response.Headers.ETag?.ToString());
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new HttpTransportGetResult<TFragment>(
                    StateReadResult<TFragment>.NotFound(revision),
                    revision
                );
            }

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new HttpTransportGetResult<TFragment>(
                    StateReadResult<TFragment>.Unavailable(revision),
                    revision
                );
            }

            if (
                response.StatusCode == (HttpStatusCode)422
                || response.StatusCode == HttpStatusCode.BadRequest
            )
            {
                return new HttpTransportGetResult<TFragment>(
                    StateReadResult<TFragment>.InvalidPayload(default, revision),
                    revision
                );
            }

            if (HttpStateProtocol.IsTemporarilyUnavailable(response.StatusCode))
            {
                return new HttpTransportGetResult<TFragment>(
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
            var content = await HttpStateProtocol
                .ReadContentAsync(response, timeout.Token)
                .ConfigureAwait(false);
            if (
                content.Length == 0
                || !TryParseFragment(content, out var fragment)
                || fragment is null
            )
            {
                return new HttpTransportGetResult<TFragment>(
                    StateReadResult<TFragment>.InvalidPayload(default, revision),
                    revision,
                    null
                );
            }

            return new HttpTransportGetResult<TFragment>(
                StateReadResult<TFragment>.Success(fragment, revision),
                revision,
                content
            );
        }
    }

    public async ValueTask<HttpTransportPutResult> PutAsync(
        byte[] body,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken
    )
    {
        using var timeout = HttpStateProtocol.CreateRequestCancellation(
            _requestTimeout,
            cancellationToken
        );
        var httpResponse = await HttpStateProtocol
            .SendWithSharedTimeoutAsync(
                _httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Put, _endpoint)
                    {
                        Content = new ByteArrayContent(body),
                    };
                    message.Content.Headers.ContentType = new MediaTypeHeaderValue(
                        "application/json"
                    );
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

                    return message;
                },
                timeout.Token,
                cancellationToken
            )
            .ConfigureAwait(false);

        using (httpResponse)
        {
            var revision = HttpStateProtocol.ParseEtagToRevision(
                httpResponse.Headers.ETag?.ToString()
            );
            if (
                httpResponse.StatusCode == HttpStatusCode.OK
                || httpResponse.StatusCode == HttpStatusCode.NoContent
            )
            {
                var content = await HttpStateProtocol
                    .ReadContentAsync(httpResponse, timeout.Token)
                    .ConfigureAwait(false);
                return new HttpTransportPutResult(revision, content);
            }

            var detail = await HttpStateProtocol
                .ReadProblemDetailAsync(httpResponse, timeout.Token)
                .ConfigureAwait(false);
            throw MapWriteError(httpResponse.StatusCode, detail);
        }
    }

    public async ValueTask<HttpTransportPatchResult<TFragment>> PatchAsync(
        byte[] patchBody,
        string revision,
        CancellationToken cancellationToken
    )
    {
        using var timeout = HttpStateProtocol.CreateRequestCancellation(
            _requestTimeout,
            cancellationToken
        );
        var httpResponse = await HttpStateProtocol
            .SendWithSharedTimeoutAsync(
                _httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(new HttpMethod("PATCH"), _endpoint)
                    {
                        Content = new ByteArrayContent(patchBody),
                    };
                    message.Content.Headers.ContentType = new MediaTypeHeaderValue(
                        "application/json-patch+json"
                    );
                    message.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{revision}\""));

                    return message;
                },
                timeout.Token,
                cancellationToken
            )
            .ConfigureAwait(false);

        using (httpResponse)
        {
            var responseRevision = HttpStateProtocol.ParseEtagToRevision(
                httpResponse.Headers.ETag?.ToString()
            );
            if (httpResponse.StatusCode == HttpStatusCode.OK)
            {
                var content = await HttpStateProtocol
                    .ReadContentAsync(httpResponse, timeout.Token)
                    .ConfigureAwait(false);
                _ = TryParseFragment(content, out var fragment);
                return new HttpTransportPatchResult<TFragment>(fragment, responseRevision, content);
            }

            if (httpResponse.StatusCode == HttpStatusCode.NoContent)
            {
                return new HttpTransportPatchResult<TFragment>(default, responseRevision, null);
            }

            if (
                httpResponse.StatusCode == HttpStatusCode.NotFound
                || httpResponse.StatusCode == HttpStatusCode.MethodNotAllowed
            )
            {
                throw new PatchUnsupportedException(
                    "The State HTTP endpoint does not support PATCH."
                );
            }

            var detail = await HttpStateProtocol
                .ReadProblemDetailAsync(httpResponse, timeout.Token)
                .ConfigureAwait(false);
            switch (httpResponse.StatusCode)
            {
                case HttpStatusCode.BadRequest:
                    throw new HttpStateRequestException(
                        "The server rejected the JSON Patch document: " + detail
                    );
                case (HttpStatusCode)428:
                    throw new HttpStateRequestException(
                        "The State HTTP PATCH endpoint requires an If-Match ETag: " + detail
                    );
                case (HttpStatusCode)422:
                    throw new HttpStateValidationException(
                        "The server rejected the patched state value: " + detail,
                        HttpStateProtocol.SplitFailures(detail)
                    );
                case HttpStatusCode.PreconditionFailed:
                    throw new HttpStateStaleException(
                        "The effective state changed after it was read (stale If-Match)."
                    );
                case HttpStatusCode.Conflict:
                    throw new HttpStateWriteConflictException(
                        "The state patch conflicts with the current state: " + detail
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
                    if (HttpStateProtocol.IsTemporarilyUnavailable(httpResponse.StatusCode))
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

    public bool TryParseFragment(byte[] content, out TFragment? fragment)
    {
        fragment = null;
        try
        {
            var converter = ConfiglueJsonFragmentRegistry<TFragment>.Converter;
            var options = ConfiglueFragmentJson.CreateOptions(_serializerOptions);
            var jsonReader = new Utf8JsonReader(content);
            if (!jsonReader.Read())
            {
                return false;
            }

            fragment = converter.Read(ref jsonReader, typeof(TFragment), options);
            return fragment is not null;
        }
        catch (JsonException)
        {
            fragment = null;
            return false;
        }
    }

    private static Exception MapWriteError(HttpStatusCode statusCode, string detail)
    {
        switch (statusCode)
        {
            case HttpStatusCode.BadRequest:
                return new HttpStateRequestException(
                    "The server rejected the state payload: " + detail
                );
            case (HttpStatusCode)422:
                return new HttpStateValidationException(
                    "The server rejected the state value: " + detail,
                    HttpStateProtocol.SplitFailures(detail)
                );
            case HttpStatusCode.PreconditionFailed:
                return new HttpStateStaleException(
                    "The effective state changed after it was read (stale If-Match)."
                );
            case HttpStatusCode.Conflict:
                return new HttpStateWriteConflictException(
                    "The state write conflicts with a concurrent change: " + detail
                );
            case HttpStatusCode.Unauthorized:
                return new HttpStateUnauthorizedException(
                    "The State HTTP endpoint requires authentication."
                );
            case HttpStatusCode.Forbidden:
                return new HttpStateForbiddenException(
                    "The State HTTP endpoint denied the request."
                );
            default:
                if (HttpStateProtocol.IsTemporarilyUnavailable(statusCode))
                {
                    return new HttpStateUnavailableException(
                        $"The State HTTP endpoint returned {(int)statusCode}: {detail}"
                    );
                }

                return new HttpStateException(
                    $"Unexpected State HTTP status {(int)statusCode}: {detail}"
                );
        }
    }
}

internal readonly record struct HttpTransportGetResult<TFragment>(
    StateReadResult<TFragment> Result,
    string? Revision,
    byte[]? Content = null
)
    where TFragment : class, IConfiglueFragment<TFragment>;

internal readonly record struct HttpTransportPutResult(string? Revision, byte[] Content);

internal readonly record struct HttpTransportPatchResult<TFragment>(
    TFragment? Value,
    string? Revision,
    byte[]? Content
)
    where TFragment : class, IConfiglueFragment<TFragment>;
