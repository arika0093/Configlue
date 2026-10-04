using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Configlue.Source.Http;

/// <summary>Single SSE invalidation connection for the State HTTP events endpoint.</summary>
/// <remarks>Owns only event-stream parsing; reconnect/backoff lives in the watch loop.</remarks>
internal sealed class HttpSseClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _eventsEndpoint;

    public HttpSseClient(HttpClient httpClient, Uri eventsEndpoint)
    {
        _httpClient = httpClient;
        _eventsEndpoint = eventsEndpoint;
    }

    /// <summary>Waits for the next <c>changed</c> SSE event, returning its id.</summary>
    public async ValueTask<string?> WaitForInvalidationAsync(
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
}
