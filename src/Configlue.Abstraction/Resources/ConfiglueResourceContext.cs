namespace Configlue.Resources;

/// <summary>Identifies the logical subject and source-specific key for a resource operation.</summary>
public readonly record struct ConfiglueResourceContext
{
    /// <summary>Creates context for one logical subject and its source-specific key.</summary>
    public ConfiglueResourceContext(IConfiglueSubject Subject, SubjectKey Key)
    {
        ArgumentNullException.ThrowIfNull(Subject);
        this.Subject = Subject;
        this.Key = Key;
    }

    /// <summary>The application-defined subject.</summary>
    public IConfiglueSubject Subject { get; init; }

    /// <summary>The source-specific canonical key for this operation.</summary>
    public SubjectKey Key { get; init; }

    /// <summary>Context used by legacy, server-wide resource operations.</summary>
    public static ConfiglueResourceContext Default { get; } =
        new(DefaultSubject.Instance, SubjectKey.Default);

    private sealed class DefaultSubject : IConfiglueSubject
    {
        public static DefaultSubject Instance { get; } = new();

        public SubjectKey Key => SubjectKey.Default;
    }
}
