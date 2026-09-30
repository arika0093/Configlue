namespace Configlue;

/// <summary>Identifies a named Configlue state profile registered with dependency injection.</summary>
public sealed record ConfiglueNamedStateProfile<TModel>
{
    /// <summary>The registered profile name.</summary>
    public string Name { get; init; }

    /// <summary>Creates a profile marker for a model.</summary>
    public ConfiglueNamedStateProfile(string Name)
    {
        this.Name = Name;
    }

    /// <summary>The model type associated with this profile.</summary>
    public Type ModelType => typeof(TModel);
}
