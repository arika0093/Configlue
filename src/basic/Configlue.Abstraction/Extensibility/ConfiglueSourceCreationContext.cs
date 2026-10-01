namespace Configlue.Extensibility;

/// <summary>Context supplied to a provider when its source is materialized.</summary>
public sealed class ConfiglueSourceCreationContext
{
    private readonly List<object> _resources = [];

    internal ConfiglueSourceCreationContext(
        ConfiglueModelSchema modelSchema,
        IServiceProvider? services,
        IConfiglueHostPaths hostPaths
    )
    {
        ModelSchema = modelSchema;
        Services = services;
        HostPaths = hostPaths;
    }

    /// <summary>Generated metadata for the source's model.</summary>
    public ConfiglueModelSchema ModelSchema { get; }

    /// <summary>The application's services, or null for an independent context.</summary>
    public IServiceProvider? Services { get; }

    /// <summary>The active host's standard storage locations.</summary>
    public IConfiglueHostPaths HostPaths { get; }

    /// <summary>Declares a resource created by the provider as context-owned.</summary>
    /// <remarks>
    /// The resource must implement <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>.
    /// Asynchronous disposal is preferred for resources implementing both. Application-supplied
    /// resources must remain borrowed and must not be registered here.
    /// </remarks>
    public void Own(object resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource is not IDisposable && resource is not IAsyncDisposable)
        {
            throw new ArgumentException(
                "An owned resource must implement IDisposable or IAsyncDisposable.",
                nameof(resource)
            );
        }

        _resources.Add(resource);
    }

    /// <summary>Completes source creation, explicitly reporting its context-owned resources.</summary>
    public ConfiglueSourceCreation<T> Complete<T>(StateSource<T> source) =>
        new(source, _resources.ToArray());

    internal IReadOnlyList<object> CreatedResources => _resources;
}

/// <summary>A provider-created source and the resources created for its context lifetime.</summary>
/// <typeparam name="T">The generated fragment type.</typeparam>
public sealed class ConfiglueSourceCreation<T>
{
    /// <summary>Creates a result. Omitted resources are borrowed from the application.</summary>
    /// <remarks>Owned resources must implement <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>.</remarks>
    public ConfiglueSourceCreation(
        StateSource<T> source,
        IReadOnlyList<object>? ownedResources = null
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        OwnedResources = Array.AsReadOnly(ownedResources?.ToArray() ?? []);
    }

    /// <summary>The logical source.</summary>
    public StateSource<T> Source { get; }

    /// <summary>Provider-created resources to release with the context.</summary>
    public IReadOnlyList<object> OwnedResources { get; }
}
