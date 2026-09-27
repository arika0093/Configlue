namespace Configlue;

/// <summary>A source identity associated with one configuration model type.</summary>
/// <typeparam name="TModel">The configuration model type.</typeparam>
public readonly record struct SourceKey<TModel>
{
    private SourceKey(string id) => Id = id;

    internal string Id { get; }

    /// <summary>Creates a typed key for a registered logical source identifier.</summary>
    public static SourceKey<TModel> FromId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new SourceKey<TModel>(id);
    }
}
