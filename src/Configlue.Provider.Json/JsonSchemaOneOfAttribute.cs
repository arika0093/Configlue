using System;

namespace Configlue.Provider.Json;

/// <summary>
/// Describes the alternative JSON shapes accepted by a property with a custom converter.
/// </summary>
/// <remarks>
/// This attribute applies when the converter exports a boolean schema. Non-primitive alternatives
/// must be available from the configured JSON type-info resolver.
/// </remarks>
/// <param name="types">CLR types whose JSON schemas form the alternatives.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class JsonSchemaOneOfAttribute(params Type[] types) : Attribute
{
    /// <summary>Gets the CLR types whose JSON schemas are included in the oneOf schema.</summary>
    public Type[] Types { get; } = types ?? throw new ArgumentNullException(nameof(types));
}
