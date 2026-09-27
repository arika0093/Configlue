using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Configlue.Resource.Http;

/// <summary>Adds HTTP state sources backed by clients managed by <see cref="IHttpClientFactory"/>.</summary>
public static class HttpClientFactorySourceExtensions
{
    /// <summary>Registers a source using a named client from the current service provider.</summary>
    /// <remarks>
    /// The named client is resolved when the Configlue context is created. Client instances are returned to
    /// the factory's normal lifetime management and are not disposed by the source.
    /// </remarks>
    public static void FromHttpClientFactory(
        this ConfiglueSourceSetBuilder sources,
        string clientName,
        HttpSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Client is not null || options.ClientFactory is not null)
        {
            throw new ArgumentException(
                "The source options must not already contain a client or client factory.",
                nameof(options)
            );
        }

        sources.FromHttp(
            new HttpSourceOptions
            {
                Id = options.Id,
                EndPoint = options.EndPoint,
                Codec = options.Codec,
                Priority = options.Priority,
                FallbackCondition = options.FallbackCondition,
                Writable = options.Writable,
                WatchChanges = options.WatchChanges,
                ResourceOptions = options.ResourceOptions,
                ResourceId = options.ResourceId,
                CodecContext = options.CodecContext,
                ClientFactory = provider =>
                    (
                        provider
                        ?? throw new InvalidOperationException(
                            "A named HTTP client requires a service provider."
                        )
                    )
                        .GetRequiredService<IHttpClientFactory>()
                        .CreateClient(clientName),
            }
        );
    }
}
