using System.Net;
using Configlue;

namespace Example.MultiSource;

internal sealed class HttpJsonResource(HttpClient httpClient, Uri uri) : IResourceReader
{
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            using var response = await httpClient.GetAsync(uri, cancellationToken);
            var revision = response.Headers.ETag?.ToString();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A missing remote policy lets the lower-priority file sources contribute.
                return ResourceReadResult.NotFound(revision);
            }

            if (!response.IsSuccessStatusCode)
            {
                // Treat failed HTTP responses as temporary unavailability for source fallback.
                return ResourceReadResult.Unavailable(revision);
            }

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return ResourceReadResult.Success(content, revision);
        }
        catch (HttpRequestException)
        {
            return ResourceReadResult.Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ResourceReadResult.Unavailable();
        }
    }
}
