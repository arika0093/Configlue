namespace Configlue;

/// <summary>A source identity associated with one configuration model type.</summary>
/// <remarks>The default value is uninitialized and is rejected where a source key is required.
/// Advanced vocabulary: only explicit per-source addressing needs this type.</remarks>
/// <typeparam name="TModel">The configuration model type.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly record struct SourceKey<TModel>
{
    private readonly SourceId _id;

    private SourceKey(SourceId id) => _id = id;

    internal SourceId Id => _id;

    /// <summary>The stable application-defined logical source name, or an empty string for default.</summary>
    public string Name => _id.Value;

    /// <summary>Whether this key is the uninitialized default value.</summary>
    public bool IsDefault => _id.IsDefault;

    /// <summary>Creates a new opaque key for explicitly registered sources.</summary>
    public static SourceKey<TModel> Create() => new(SourceId.From($"source:{Guid.NewGuid():N}"));

    /// <summary>Creates a typed key for a stable, application-defined logical source name.</summary>
    public static SourceKey<TModel> Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new SourceKey<TModel>(SourceId.From(name));
    }
}
