using System;

namespace Configlue.Provider.Json;

/// <summary>
/// Supplies a JSON Schema for a property whose custom JSON converter exports a boolean schema.
/// </summary>
/// <param name="schema">A JSON Schema object or boolean, encoded as JSON.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class JsonSchemaOverrideAttribute(string schema) : Attribute
{
    /// <summary>Gets the JSON-encoded schema to use for the property.</summary>
    public string Schema { get; } = schema ?? throw new ArgumentNullException(nameof(schema));
}
