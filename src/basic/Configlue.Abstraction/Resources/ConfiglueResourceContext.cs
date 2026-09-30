namespace Configlue.Resources;

/// <summary>Identifies the stable model, logical subject, and source-specific key for a resource operation.</summary>
public readonly record struct ConfiglueResourceContext
{
    /// <summary>Creates context for one logical subject and its source-specific key.</summary>
    public ConfiglueResourceContext(IConfiglueSubject Subject, SubjectKey Key)
        : this(Subject, Key, RouteKey.Default) { }

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
    public IConfiglueSubject Subject { get; init; }

    /// <summary>The source-specific canonical key for this operation.</summary>
    public SubjectKey Key { get; init; }

    /// <summary>The opaque physical placement route for this operation.</summary>
    public RouteKey Route { get; init; }

    /// <summary>The subject used by server-wide resource operations.</summary>
    internal static IConfiglueSubject DefaultSubject { get; } = DefaultSubjectInstance.Instance;

    /// <summary>Context used by legacy, server-wide resource operations.</summary>
    public static ConfiglueResourceContext Default { get; } =
        new(DefaultSubject, SubjectKey.Default);

    private sealed class DefaultSubjectInstance : IConfiglueSubject
    {
        public static DefaultSubjectInstance Instance { get; } = new();

        public SubjectKey Key => SubjectKey.Default;
    }
}
