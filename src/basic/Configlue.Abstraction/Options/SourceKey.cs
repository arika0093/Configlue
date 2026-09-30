namespace Configlue;

/// <summary>A source identity associated with one configuration model type.</summary>
/// <typeparam name="TModel">The configuration model type.</typeparam>
public readonly record struct SourceKey<TModel>
{
    private SourceKey(string name)
    {
        Name = name;
        Id = name;
    }

    internal string Id { get; }

    /// <summary>The stable application-defined logical source name.</summary>
    public string Name { get; }

    /// <summary>Creates a new opaque key for explicitly registered sources.</summary>
    public static SourceKey<TModel> Create() => new($"source:{Guid.NewGuid():N}");

    /// <summary>Creates a typed key for a stable, application-defined logical source name.</summary>
    public static SourceKey<TModel> Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new SourceKey<TModel>(name);
    }
}
