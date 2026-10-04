using System.Net;
using System.Text;
using System.Text.Json;

namespace Configlue.Source.Http;

/// <summary>Shared State HTTP wire helpers: ETag handling, timeout scope, and error bodies.</summary>
/// <remarks>Centralizes request timeout and error-response handling for GET/PUT/PATCH.</remarks>
internal static class HttpStateProtocol
{
    public static CancellationTokenSource CreateRequestCancellation(
        TimeSpan requestTimeout,
        CancellationToken cancellationToken
    )
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(requestTimeout);
        return linked;
    }

    public static TimeSpan NextBackoff(TimeSpan current, TimeSpan max)
    {
        return TimeSpan.FromTicks(Math.Min(current.Ticks * 2, max.Ticks));
    }

    public static bool IsTemporarilyUnavailable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout
        || (int)statusCode == 429
        || (int)statusCode is >= 500 and <= 599;

    public static string? ParseEtagToRevision(string? etag)
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

    public static string NormalizeEtagArgument(string etag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        var trimmed = etag.Trim();
        if (IsBareRevision(trimmed))
        {
            return trimmed.ToLowerInvariant();
        }

        var normalized = ParseEtagToRevision(trimmed);
        if (normalized is null)
        {
            throw new ArgumentException(
                "The ETag must be a 64-character hex state revision, quoted or unquoted.",
                nameof(etag)
            );
        }

        return normalized;
    }

    public static bool IsBareRevision(string value) =>
        value.Length == 64 && value.All(static c => Uri.IsHexDigit(c));

    public static IReadOnlyList<string> SplitFailures(string detail)
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

    public static ResourceId CreateResourceId(Uri endpoint, Uri eventsEndpoint)
    {
        var identity = Encoding.UTF8.GetBytes(
            endpoint.AbsoluteUri + "\n" + eventsEndpoint.AbsoluteUri
        );
        return new ResourceId(
            "http-state:"
                + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(identity))
        );
    }

    public static async ValueTask<byte[]> ReadContentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        try
        {
#if NETSTANDARD2_0
            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#else
            return await response
                .Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
#endif
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    public static async ValueTask<string> ReadProblemDetailAsync(
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

    /// <summary>Sends one request with unified transport/timeout mapping.</summary>
    /// <exception cref="HttpStateUnavailableException">Transport or timeout failure.</exception>
    public static async ValueTask<HttpResponseMessage> SendWithTimeoutAsync(
        HttpClient httpClient,
        Func<HttpRequestMessage> messageFactory,
        TimeSpan requestTimeout,
        CancellationToken callerToken
    )
    {
        using var message = messageFactory();
        using var timeout = CreateRequestCancellation(requestTimeout, callerToken);
        try
        {
            return await httpClient
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
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw new HttpStateUnavailableException("The State HTTP request timed out.", exception);
        }
    }
}

/// <summary>Signals a PATCH fallback to PUT when the server does not map the patch endpoint.</summary>
internal sealed class PatchUnsupportedException(string message) : HttpStateException(message);
