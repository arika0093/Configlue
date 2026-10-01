using System.Collections.Concurrent;

namespace Configlue.CompilerServices;

/// <summary>Provides generated model schemas by model type without a static generic constraint.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class ConfiglueModelSchemaCatalog
{
    private static readonly ConcurrentDictionary<Type, ConfiglueModelSchema> Schemas = new();

    /// <summary>Registers the generated schema for a model type.</summary>
    public static void Register(Type modelType, ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(modelType);
        ArgumentNullException.ThrowIfNull(schema);
        Schemas[modelType] = schema;
    }

    /// <summary>Attempts to get the generated schema registered for a model type.</summary>
    public static bool TryGet(Type modelType, out ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(modelType);
        return Schemas.TryGetValue(modelType, out schema!);
    }
}
