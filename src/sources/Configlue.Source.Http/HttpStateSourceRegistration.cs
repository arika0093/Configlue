using Configlue.CompilerServices;
using Configlue.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Source.Http;

/// <summary>Registers facade sources backed by the typed Configlue State HTTP protocol.</summary>
public static class HttpStateSourceRegistration
{
    /// <summary>Adds a State HTTP source. Clients and factory-provided handlers remain externally owned.</summary>
    public static ConfiglueSourceRegistration FromHttpState(
        this ConfiglueSourceSetBuilder sources,
        HttpStateSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EndPoint);
        var endpoint = ValidateEndpoint(options.EndPoint, nameof(options));
        var eventsEndpoint = options.EventsEndPoint is null
            ? DeriveEventsEndpoint(endpoint)
            : ValidateEndpoint(options.EventsEndPoint, nameof(options));
        if (options.Client is not null && options.ClientFactory is not null)
        {
            throw new ArgumentException(
                "Configure either Client or ClientFactory, not both.",
                nameof(options)
            );
        }

        if (options.Client is null && options.ClientFactory is null)
        {
            throw new ArgumentException(
                "Configure a direct client or a provider-aware client factory.",
                nameof(options)
            );
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "RequestTimeout must be greater than zero."
            );
        }

        if (options.ReconnectInitialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.ReconnectMaxDelay < options.ReconnectInitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new HttpStateSourceDefinition(options, endpoint, eventsEndpoint)
        );
    }

    /// <summary>Registers a source using a named client from the current service provider.</summary>
    /// <remarks>
    /// The named client is resolved when the Configlue context is created. Client instances are returned to
    /// the factory's normal lifetime management and are not disposed by the source.
    /// </remarks>
    public static void FromHttpStateClientFactory(
        this ConfiglueSourceSetBuilder sources,
        string clientName,
        HttpStateSourceOptions options
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

        sources.FromHttpState(
            new HttpStateSourceOptions
            {
                Id = options.Id,
                EndPoint = options.EndPoint,
                EventsEndPoint = options.EventsEndPoint,
                Priority = options.Priority,
                FallbackCondition = options.FallbackCondition,
                Writable = options.Writable,
                WatchChanges = options.WatchChanges,
                RequestTimeout = options.RequestTimeout,
                ReconnectInitialDelay = options.ReconnectInitialDelay,
                ReconnectMaxDelay = options.ReconnectMaxDelay,
                SerializerOptions = options.SerializerOptions,
                ClientFactory = provider =>
                    (
                        provider
                        ?? throw new InvalidOperationException(
                            "A named HTTP client requires a service provider."
                        )
                    )
                        .GetRequiredService<System.Net.Http.IHttpClientFactory>()
                        .CreateClient(clientName),
            }
        );
    }

    /// <summary>Registers a State HTTP source as the model's source.</summary>
    public static void UseHttpState<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        string endpoint,
        Action<HttpStateSourceOptions>? configure = null
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        var options = new HttpStateSourceOptions { EndPoint = endpoint };
        configure?.Invoke(options);
        model.Sources(sources => sources.FromHttpState(options));
        if (options.Writable && options.Id is { } id)
        {
            model.Writes(write => write.DefaultTo(SourceKey<TModel>.Named(id)));
        }
    }

    /// <summary>Registers a State HTTP source as the model's source.</summary>
    public static void UseHttpState<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        HttpStateSourceOptions options,
        Action<HttpStateSourceOptions>? configure = null
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        configure?.Invoke(options);
        model.Sources(sources => sources.FromHttpState(options));
        if (options.Writable && options.Id is { } id)
        {
            model.Writes(write => write.DefaultTo(SourceKey<TModel>.Named(id)));
        }
    }

    /// <summary>Registers a State HTTP source using a named client as the model's source.</summary>
    public static void UseHttpStateClientFactory<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        string clientName,
        HttpStateSourceOptions options,
        Action<HttpStateSourceOptions>? configure = null
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentNullException.ThrowIfNull(options);
        configure?.Invoke(options);
        model.Sources(sources => sources.FromHttpStateClientFactory(clientName, options));
        if (options.Writable && options.Id is { } id)
        {
            model.Writes(write => write.DefaultTo(SourceKey<TModel>.Named(id)));
        }
    }

    private static Uri ValidateEndpoint(string value, string parameterName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("EndPoint must be an absolute URI.", parameterName);
        }

        if (
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new ArgumentException("EndPoint must use HTTP or HTTPS.", parameterName);
        }

        if (endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0)
        {
            throw new ArgumentException(
                "Credentials and fragments must be configured on HttpClient, not the endpoint.",
                parameterName
            );
        }

        return endpoint;
    }

    private static Uri DeriveEventsEndpoint(Uri endpoint)
    {
        var baseUri = endpoint.AbsoluteUri.TrimEnd('/') + "/events";
        return new Uri(baseUri, UriKind.Absolute);
    }

    private sealed class HttpStateSourceDefinition(
        HttpStateSourceOptions options,
        Uri endpoint,
        Uri eventsEndpoint
    ) : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            var client = options.Client ?? options.ClientFactory!(context.Services);
            if (client is null)
            {
                throw new InvalidOperationException("The HTTP client factory returned null.");
            }

            var reader = new HttpStateReader<TFragment>(
                client,
                endpoint,
                eventsEndpoint,
                options.SerializerOptions,
                options.RequestTimeout,
                options.ReconnectInitialDelay,
                options.ReconnectMaxDelay,
                options.Writable,
                options.WatchChanges
            );
            ISourceWriter<TFragment>? writer = options.Writable ? reader : null;
            ISourceWatcher? watcher = options.WatchChanges ? reader : null;
            StateSource<TFragment> source;
            if (options.Id is { } id)
            {
                source = new StateSource<TFragment>(
                    id,
                    reader,
                    new StateSourceOptions<TFragment>
                    {
                        Priority = options.Priority,
                        FallbackCondition = options.FallbackCondition,
                        PhysicalOrigin = endpoint.AbsoluteUri,
                        Writer = writer,
                        DisableWriteCapability = !options.Writable,
                        Watcher = watcher,
                    }
                );
            }
            else
            {
                source = new StateSource<TFragment>(
                    reader,
                    new StateSourceOptions<TFragment>
                    {
                        Priority = options.Priority,
                        FallbackCondition = options.FallbackCondition,
                        PhysicalOrigin = endpoint.AbsoluteUri,
                        Writer = writer,
                        DisableWriteCapability = !options.Writable,
                        Watcher = watcher,
                        LogicalDescriptor = string.Join(
                            "\n",
                            options.EndPoint,
                            eventsEndpoint.AbsoluteUri
                        ),
                    }
                );
            }

            return context.Complete(source);
        }
    }
}
