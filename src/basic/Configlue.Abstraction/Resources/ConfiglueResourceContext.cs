namespace Configlue.Resources;

/// <summary>Identifies the stable model, logical subject, and source-specific key for a resource operation.</summary>
/// <remarks>The zero-initialized value is the canonical server-wide default context. A context with
/// model, key, or route values requires an explicit subject; incomplete contexts are rejected.</remarks>
public readonly record struct ConfiglueResourceContext
{
    private readonly IConfiglueSubject? _subject;

    /// <summary>Creates context for one logical subject, source-specific key, and physical route.</summary>
    public ConfiglueResourceContext(IConfiglueSubject Subject, SubjectKey Key, RouteKey Route)
        : this(null, Subject, Key, Route) { }

    /// <summary>Creates context for one stable model, logical subject, source-specific key, and physical route.</summary>
    public ConfiglueResourceContext(
        string? ModelId,
        IConfiglueSubject Subject,
        SubjectKey Key,
        RouteKey Route
    )
    {
        ArgumentNullException.ThrowIfNull(Subject);
        this.ModelId = ModelId;
        this.Subject = Subject;
        this.Key = Key;
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

    /// <summary>The source-specific canonical key for this operation.</summary>
    public SubjectKey Key { get; init; }

    /// <summary>The opaque physical placement route for this operation.</summary>
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

        if (context.ModelId is not null || context.Key != default || context.Route != default)
        {
            throw new ArgumentException(
                "A resource context without a subject cannot contain model, key, or route values.",
                nameof(context)
            );
        }

        return Default;
    }

    /// <summary>The subject used by server-wide resource operations.</summary>
    internal static IConfiglueSubject DefaultSubject { get; } = DefaultSubjectInstance.Instance;

    /// <summary>Context used by legacy, server-wide resource operations.</summary>
    public static ConfiglueResourceContext Default { get; } =
        new(DefaultSubject, SubjectKey.Default, RouteKey.Default);

    /// <inheritdoc />
    public bool Equals(ConfiglueResourceContext other) =>
        ModelId == other.ModelId
        && EqualityComparer<IConfiglueSubject>.Default.Equals(
            _subject ?? DefaultSubject,
            other._subject ?? DefaultSubject
        )
        && Key == other.Key
        && Route == other.Route;

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = ModelId is null ? 0 : StringComparer.Ordinal.GetHashCode(ModelId);
        hash = unchecked(
            hash * 31
            + EqualityComparer<IConfiglueSubject>.Default.GetHashCode(_subject ?? DefaultSubject)
        );
        hash = unchecked(hash * 31 + Key.GetHashCode());
        return unchecked(hash * 31 + Route.GetHashCode());
    }

    private sealed class DefaultSubjectInstance : IConfiglueSubject
    {
        public static DefaultSubjectInstance Instance { get; } = new();

        public SubjectKey Key => SubjectKey.Default;
    }
}
