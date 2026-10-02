namespace Configlue;

/// <summary>A source identity associated with one configuration model type.</summary>
/// <remarks>The default value is uninitialized and is rejected where a source key is required.</remarks>
/// <typeparam name="TModel">The configuration model type.</typeparam>
public readonly record struct SourceKey<TModel>
{
    private readonly string? _name;

    private SourceKey(string name) => _name = name;

    internal string Id => _name ?? string.Empty;

    /// <summary>The stable application-defined logical source name, or an empty string for default.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>Whether this key is the uninitialized default value.</summary>
    public bool IsDefault => _name is null;

    /// <summary>Creates a new opaque key for explicitly registered sources.</summary>
    public static SourceKey<TModel> Create() => new($"source:{Guid.NewGuid():N}");

    /// <summary>Creates a typed key for a stable, application-defined logical source name.</summary>
    public static SourceKey<TModel> Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new SourceKey<TModel>(name);
    }
}
