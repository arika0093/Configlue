#pragma warning disable S1075 // Agent endpoint paths use RFC 3986 forward slashes; never filesystem paths.

using System.Net;

namespace Configlue.Resource.AwsAppConfig;

/// <summary>The result of one AppConfig Agent endpoint poll.</summary>
/// <param name="Configuration">The payload. Empty when the agent reports no configuration.</param>
/// <param name="Version">The agent-reported configuration version, when provided.</param>
/// <param name="NotFound">Whether the agent reports no deployed configuration.</param>
internal sealed record AppConfigAgentFetchResult(
    ReadOnlyMemory<byte> Configuration,
    string? Version,
    bool NotFound
);

/// <summary>Fetches configuration from the local AppConfig Agent endpoint. Implemented by fakes in tests.</summary>
internal interface IAwsAppConfigAgentFetcher
{
    /// <summary>Fetches the current configuration from the agent.</summary>
    Task<AppConfigAgentFetchResult> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>HTTP implementation of the AppConfig Agent endpoint fetch.</summary>
internal sealed class AwsAppConfigAgentHttpFetcher : IAwsAppConfigAgentFetcher
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string? _clientId;

    public AwsAppConfigAgentHttpFetcher(HttpClient client, Uri endpoint, string? clientId)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        _client = client;
        _endpoint = endpoint;
        _clientId = clientId;
    }

    public static Uri BuildEndpoint(
        Uri baseAddress,
        string applicationId,
        string environmentId,
        string configurationProfileId
    )
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationProfileId);
        var baseUri = baseAddress.AbsoluteUri.TrimEnd('/') + "/";
        return new Uri(
            baseUri
                + "applications/"
                + Uri.EscapeDataString(applicationId)
                + "/environments/"
                + Uri.EscapeDataString(environmentId)
                + "/configurations/"
                + Uri.EscapeDataString(configurationProfileId),
            UriKind.Absolute
        );
    }

    public async Task<AppConfigAgentFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        if (_clientId is not null)
        {
            request.Headers.TryAddWithoutValidation("Entity-Id", _clientId);
        }

        HttpResponseMessage response;
        try
        {
#if NETSTANDARD2_0
            response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
#else
            response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
#endif
        }
        catch (HttpRequestException exception)
        {
            throw new AwsAppConfigTransientException(
                "The AppConfig Agent endpoint is unavailable.",
                exception
            );
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new AwsAppConfigTransientException(
                "The AppConfig Agent request timed out.",
                exception
            );
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new AppConfigAgentFetchResult(default, null, NotFound: true);
            }

            if (
                response.StatusCode == HttpStatusCode.RequestTimeout
                || (int)response.StatusCode == 429
                || (int)response.StatusCode is >= 500 and <= 599
            )
            {
                throw new AwsAppConfigTransientException(
                    $"The AppConfig Agent endpoint returned {(int)response.StatusCode}."
                );
            }

            if (
                response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden
            )
            {
                throw new AwsAppConfigException(
                    $"The AppConfig Agent endpoint denied the request ({(int)response.StatusCode})."
                );
            }

            response.EnsureSuccessStatusCode();
            var version = GetVersion(response);
#if NETSTANDARD2_0
            var content = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#else
            var content = await response
                .Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
#endif
            if (content.Length == 0)
            {
                return new AppConfigAgentFetchResult(default, version, NotFound: false);
            }

            return new AppConfigAgentFetchResult(content, version, NotFound: false);
        }
    }

    private static string? GetVersion(HttpResponseMessage response)
    {
        foreach (var header in response.Headers)
        {
            if (
                header.Key.Equals("Configuration-Version", StringComparison.OrdinalIgnoreCase)
                && header.Value.FirstOrDefault() is { Length: > 0 } version
            )
            {
                return version;
            }
        }

        var etag = response.Headers.ETag?.Tag?.Trim('"');
        return string.IsNullOrEmpty(etag) ? null : etag;
    }
}
