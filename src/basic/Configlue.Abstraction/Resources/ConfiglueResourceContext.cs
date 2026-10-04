namespace Configlue.Resources;

/// <summary>Provides model, application subject, source-specific resource key, and placement route for one operation.</summary>
/// <remarks>The zero-initialized value is the canonical default context; populated context fields require an explicit subject.
/// Provider SPI: flows from the runtime into resource/source implementations. Application code never constructs it.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly record struct ConfiglueResourceContext
{
    private readonly IConfiglueSubject? _subject;

    /// <summary>Creates context for one logical subject, source-specific resource key, and physical route.</summary>
    public ConfiglueResourceContext(
        IConfiglueSubject Subject,
        ResourceKey ResourceKey,
        RouteKey Route
    )
        : this(null, Subject, ResourceKey, Route) { }

    /// <summary>Creates context for one stable model, logical subject, source-specific resource key, and physical route.</summary>
    public ConfiglueResourceContext(
        string? ModelId,
        IConfiglueSubject Subject,
        ResourceKey ResourceKey,
        RouteKey Route
    )
    {
        ArgumentNullException.ThrowIfNull(Subject);
        this.ModelId = ModelId;
        this.Subject = Subject;
        this.ResourceKey = ResourceKey;
        this.Route = Route;
    }

    /// <summary>The stable Configlue model ID for this operation, when known.</summary>
    public string? ModelId { get; init; }

    /// <summary>The application-defined subject.</summary>
    public IConfiglueSubject Subject
    {
        get => Normalize(this)._subject ?? DefaultSubject;
        init => _subject = value;
    }

    /// <summary>The source-specific key that the provider uses to address this operation.</summary>
    public ResourceKey ResourceKey { get; init; }

    /// <summary>The intermediate backend or placement route for this operation, not its physical resource identity.</summary>
    public RouteKey Route { get; init; }

    /// <summary>Whether this value represents the server-wide default context.</summary>
    internal bool IsDefault => _subject is null || ReferenceEquals(_subject, DefaultSubject);

    /// <summary>Normalizes the zero-initialized struct to the canonical default context.</summary>
    internal static ConfiglueResourceContext Normalize(ConfiglueResourceContext context)
    {
        if (context._subject is not null)
        {
            return context;
        }

        if (
            context.ModelId is not null
            || context.ResourceKey != default
            || context.Route != default
        )
        {
            throw new ArgumentException(
                "A resource context without a subject cannot contain model, resource key, or route values.",
                nameof(context)
            );
        }

        return Default;
    }

    /// <summary>The subject used by server-wide resource operations.</summary>
    internal static IConfiglueSubject DefaultSubject { get; } = DefaultSubjectInstance.Instance;

    /// <summary>Context used by legacy, server-wide resource operations.</summary>
    public static ConfiglueResourceContext Default { get; } =
        new(DefaultSubject, ResourceKey.Default, RouteKey.Default);

    /// <summary>
    /// Determines whether this context identifies the same operation as <paramref name="other"/>.
    /// Equality uses the ordinal model ID, <see cref="IConfiglueSubject.Key"/>, source-specific resource key,
    /// and route. Subject object equality and runtime type do not participate.
    /// </summary>
    /// <param name="other">The context to compare with this context.</param>
    /// <returns><see langword="true"/> when the stable identity values match.</returns>
    public bool Equals(ConfiglueResourceContext other) =>
        StringComparer.Ordinal.Equals(ModelId, other.ModelId)
        && (_subject ?? DefaultSubject).Key == (other._subject ?? DefaultSubject).Key
        && ResourceKey == other.ResourceKey
        && Route == other.Route;

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = ModelId is null ? 0 : StringComparer.Ordinal.GetHashCode(ModelId);
        hash = unchecked(hash * 31 + (_subject ?? DefaultSubject).Key.GetHashCode());
        hash = unchecked(hash * 31 + ResourceKey.GetHashCode());
        return unchecked(hash * 31 + Route.GetHashCode());
    }

    private sealed class DefaultSubjectInstance : IConfiglueSubject
    {
        public static DefaultSubjectInstance Instance { get; } = new();

        public SubjectKey Key => SubjectKey.Default;
    }
}
