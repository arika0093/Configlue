namespace Configlue;

/// <summary>Declares a generated model whose sparse fragments represent an earlier schema version.</summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = true,
    Inherited = false
)]
public sealed class ConfigluePreviousVersionAttribute(Type modelType) : Attribute
{
    /// <summary>The model type that describes the earlier schema version.</summary>
    public Type ModelType { get; } =
        modelType ?? throw new ArgumentNullException(nameof(modelType));
}
