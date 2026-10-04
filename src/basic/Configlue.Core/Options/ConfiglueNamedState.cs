namespace Configlue;

/// <summary>Identifies one named state instance registered with dependency injection.</summary>
/// <remarks>
/// A named state instance is addressed by <c>(TModel, StateName)</c>. This marker carries that
/// state-name identity for keyed-DI registrations; it is not a persisted profile. Persisted,
/// catalog-managed named states are exposed through <see cref="IConfiglueProfiledState{TModel}"/>.
/// </remarks>
public sealed record ConfiglueNamedState<TModel>
{
    /// <summary>The registered state name.</summary>
    public string Name { get; init; }

    /// <summary>Creates a named-state marker for a model.</summary>
    public ConfiglueNamedState(string Name)
    {
        this.Name = Name;
    }

    /// <summary>The model type associated with this named state instance.</summary>
    public Type ModelType => typeof(TModel);
}
