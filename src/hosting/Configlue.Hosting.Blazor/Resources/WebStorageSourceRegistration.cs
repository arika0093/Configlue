using Configlue.Codecs;
using Configlue.CompilerServices;
using Configlue.Provider.Json;

namespace Configlue.Hosting.Blazor;

/// <summary>Options for registering a browser storage source.</summary>
public sealed class WebStorageSourceOptions
{
    /// <summary>The browser storage area. Defaults to <see cref="WebStorageKind.Local"/>.</summary>
    public WebStorageKind Kind { get; set; } = WebStorageKind.Local;

    /// <summary>The base storage key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// The codec binding for the serialized state. When omitted, the generated JSON fragment codec is used.
    /// </summary>
    public StateCodecBinding? Codec { get; set; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; set; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; set; } = StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer.</summary>
    public bool Writable { get; set; } = true;

    /// <summary>An optional source-specific key selector, for example for per-subject keys.</summary>
    public Func<ConfiglueResourceContext, string>? KeySelector { get; set; }

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; set; }
}

/// <summary>Registers sources backed by browser <c>localStorage</c> and <c>sessionStorage</c>.</summary>
public static class WebStorageSourceRegistration
{
    /// <summary>Adds a browser <c>localStorage</c> source.</summary>
    public static ConfiglueSourceRegistration FromLocalStorage(
        this ConfiglueSourceSetBuilder sources,
        WebStorageSourceOptions options
    ) => Add(sources, options, WebStorageKind.Local);

    /// <summary>Adds a browser <c>localStorage</c> source at the supplied key.</summary>
    public static ConfiglueSourceRegistration FromLocalStorage(
        this ConfiglueSourceSetBuilder sources,
        string key
    ) => Add(sources, new WebStorageSourceOptions { Key = key }, WebStorageKind.Local);

    /// <summary>Adds a browser <c>sessionStorage</c> source.</summary>
    public static ConfiglueSourceRegistration FromSessionStorage(
        this ConfiglueSourceSetBuilder sources,
        WebStorageSourceOptions options
    ) => Add(sources, options, WebStorageKind.Session);

    /// <summary>Adds a browser <c>sessionStorage</c> source at the supplied key.</summary>
    public static ConfiglueSourceRegistration FromSessionStorage(
        this ConfiglueSourceSetBuilder sources,
        string key
    ) => Add(sources, new WebStorageSourceOptions { Key = key }, WebStorageKind.Session);

    /// <summary>Registers browser <c>localStorage</c> as the model's source and write destination.</summary>
    public static void UseLocalStorage<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        string key,
        Action<WebStorageSourceOptions>? configure = null
    )
        where TModel : IConfiglueFacadeModel<TModel> =>
        UseWebStorage(model, WebStorageKind.Local, key, configure);

    /// <summary>Registers browser <c>sessionStorage</c> as the model's source and write destination.</summary>
    public static void UseSessionStorage<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        string key,
        Action<WebStorageSourceOptions>? configure = null
    )
        where TModel : IConfiglueFacadeModel<TModel> =>
        UseWebStorage(model, WebStorageKind.Session, key, configure);

    private static void UseWebStorage<TModel>(
        ConfiglueModelBuilder<TModel> model,
        WebStorageKind kind,
        string key,
        Action<WebStorageSourceOptions>? configure
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var options = new WebStorageSourceOptions { Kind = kind, Key = key };
        configure?.Invoke(options);
        options.Kind = kind;
        model.Sources(sources =>
            ((IConfiglueSourceRegistrationSink)sources).Add(new WebStorageSourceDefinition(options))
        );
        if (options.Writable && options.Id is { } id)
        {
            model.Writes(write => write.DefaultTo(SourceKey<TModel>.Named(id)));
        }
    }

    private static ConfiglueSourceRegistration Add(
        ConfiglueSourceSetBuilder sources,
        WebStorageSourceOptions options,
        WebStorageKind kind
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        options.Kind = kind;
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Key);
        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new WebStorageSourceDefinition(options)
        );
    }

    private sealed class WebStorageSourceDefinition(WebStorageSourceOptions options)
        : IConfiglueSourceDefinition,
            IConfiglueRuntimeLifetimeSource
    {
        public RuntimeLifetimeRequirement RuntimeLifetime => RuntimeLifetimeRequirement.Scoped;

        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            var services =
                context.Services
                ?? throw new InvalidOperationException(
                    "WebStorage sources require dependency injection so they can consume the scoped IJSRuntime. "
                        + "They cannot be created in a process-wide Configlue context."
                );
            var jsRuntime =
                (Microsoft.JSInterop.IJSRuntime?)
                    services.GetService(typeof(Microsoft.JSInterop.IJSRuntime))
                ?? throw new InvalidOperationException(
                    "No IJSRuntime is registered. Browser storage requires an interactive Blazor host."
                );

            var resource = new WebStorageResource(
                jsRuntime,
                options.Kind,
                options.Key,
                options.KeySelector
            );

            var codec =
                options.Codec
                ?? StateCodecBinding.Typed(
                    JsonStateCodec<TFragment>.FromConverter(
                        ConfiglueJsonFragmentRegistry<TFragment>.Converter
                    )
                );
            var serialized = new SerializedSource<TFragment>(
                resource,
                codec,
                options.CodecContext,
                writer: options.Writable ? resource : null
            );
            var physicalOrigin =
                options.Kind == WebStorageKind.Local
                    ? "weblocal:localStorage"
                    : "weblocal:sessionStorage";
            return context.Complete(
                options.Id is { } id
                    ? new StateSource<TFragment>(
                        id,
                        serialized,
                        new StateSourceOptions<TFragment>
                        {
                            Priority = options.Priority,
                            FallbackCondition = options.FallbackCondition,
                            PhysicalOrigin = physicalOrigin,
                            RuntimeLifetime = RuntimeLifetimeRequirement.Scoped,
                        }
                    )
                    : new StateSource<TFragment>(
                        serialized,
                        new StateSourceOptions<TFragment>
                        {
                            Priority = options.Priority,
                            FallbackCondition = options.FallbackCondition,
                            PhysicalOrigin = physicalOrigin,
                            RuntimeLifetime = RuntimeLifetimeRequirement.Scoped,
                        }
                    )
            );
        }
    }
}
