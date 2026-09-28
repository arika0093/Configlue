using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Configlue.JsonSchema;

internal static class JsonSchemaValidationAttributeMapper
{
    public static void AddRequiredProperties(JsonObject schema, JsonTypeInfo typeInfo)
    {
        if (
            typeInfo.Kind != JsonTypeInfoKind.Object
            || schema["properties"] is not JsonObject properties
        )
            return;

        var required = schema["required"] as JsonArray ?? [];
        foreach (
            var property in typeInfo.Properties.Where(property =>
                properties.ContainsKey(property.Name)
                && property
                    .AttributeProvider?.GetCustomAttributes(
                        typeof(RequiredAttribute),
                        inherit: true
                    )
                    .OfType<RequiredAttribute>()
                    .Any() == true
                && !required.Any(item => item?.GetValue<string>() == property.Name)
            )
        )
        {
            required.Add(property.Name);
        }

        if (required.Count > 0)
            schema["required"] = required;
    }

    public static void Apply(
        JsonObject schema,
        ValidationAttribute attribute,
        JsonTypeInfo typeInfo
    )
    {
        switch (attribute)
        {
            case RequiredAttribute required:
                AddNotSchema(schema, new JsonObject { ["type"] = "null" });
                if (typeInfo.Type == typeof(string) && !required.AllowEmptyStrings)
                    ApplyToSchemaType(schema, "string", item => AddPattern(item, "\\S"));
                break;
            case RangeAttribute range:
                if (TryGetNumericBound(range.Minimum, out var minimum))
                    ApplyToNumericSchema(
                        schema,
                        item =>
                            SetNumericBound(
                                item,
                                range.MinimumIsExclusive ? "exclusiveMinimum" : "minimum",
                                minimum,
                                isMinimum: true
                            )
                    );
                if (TryGetNumericBound(range.Maximum, out var maximum))
                    ApplyToNumericSchema(
                        schema,
                        item =>
                            SetNumericBound(
                                item,
                                range.MaximumIsExclusive ? "exclusiveMaximum" : "maximum",
                                maximum,
                                isMinimum: false
                            )
                    );
                break;
            case StringLengthAttribute stringLength:
                ApplyToSchemaType(
                    schema,
                    "string",
                    item =>
                    {
                        SetLength(item, "minLength", stringLength.MinimumLength, isMinimum: true);
                        SetLength(item, "maxLength", stringLength.MaximumLength, isMinimum: false);
                    }
                );
                break;
            case LengthAttribute length:
                SetLengthConstraint(schema, length.MinimumLength, isMinimum: true);
                SetLengthConstraint(schema, length.MaximumLength, isMinimum: false);
                break;
            case MinLengthAttribute minLength:
                SetLengthConstraint(schema, minLength.Length, isMinimum: true);
                break;
            case MaxLengthAttribute maxLength:
                SetLengthConstraint(schema, maxLength.Length, isMinimum: false);
                break;
            case AllowedValuesAttribute allowedValues:
                SetAllowedValues(schema, allowedValues.Values, typeInfo);
                break;
            case DeniedValuesAttribute deniedValues:
                AddDeniedValues(schema, deniedValues.Values, typeInfo);
                break;
            case RegularExpressionAttribute expression:
                if (IsJsonSchemaRegularExpression(expression.Pattern))
                    ApplyToSchemaType(
                        schema,
                        "string",
                        item => AddPattern(item, expression.Pattern)
                    );
                break;
            case EmailAddressAttribute:
                ApplyToSchemaType(schema, "string", item => SetFormat(item, "email"));
                break;
            case UrlAttribute:
                ApplyToSchemaType(schema, "string", item => SetFormat(item, "uri"));
                break;
            case DataTypeAttribute dataType:
                ApplyToSchemaType(
                    schema,
                    "string",
                    item => SetDataTypeFormat(item, dataType.DataType)
                );
                break;
        }
    }

    private static void SetDataTypeFormat(JsonObject schema, DataType dataType)
    {
        var format = dataType switch
        {
            DataType.Date => "date",
            DataType.DateTime => "date-time",
            DataType.Time => "time",
            DataType.Duration => "duration",
            DataType.EmailAddress => "email",
            DataType.Url => "uri",
            _ => null,
        };
        if (format is not null)
            SetFormat(schema, format);
    }

    private static void SetFormat(JsonObject schema, string format) => schema["format"] ??= format;

    private static void SetLengthConstraint(JsonObject schema, int length, bool isMinimum)
    {
        ApplyToSchemaType(
            schema,
            "string",
            item => SetLength(item, isMinimum ? "minLength" : "maxLength", length, isMinimum)
        );
        ApplyToSchemaType(
            schema,
            "array",
            item => SetLength(item, isMinimum ? "minItems" : "maxItems", length, isMinimum)
        );
    }

    private static void ApplyToNumericSchema(JsonObject schema, Action<JsonObject> apply)
    {
        ApplyToSchemaType(schema, "integer", apply);
        ApplyToSchemaType(schema, "number", apply);
    }

    private static void ApplyToSchemaType(JsonObject schema, string type, Action<JsonObject> apply)
    {
        var typeNode = schema["type"];
        if (
            typeNode is JsonValue typeValue
                && typeValue.TryGetValue<string>(out var schemaType)
                && schemaType == type
            || typeNode is JsonArray types && types.Any(item => item?.GetValue<string>() == type)
        )
        {
            apply(schema);
            return;
        }

        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (schema[keyword] is not JsonArray alternatives)
                continue;

            foreach (var alternative in alternatives.OfType<JsonObject>())
                ApplyToSchemaType(alternative, type, apply);
        }
    }

    private static void SetLength(JsonObject schema, string keyword, int length, bool isMinimum)
    {
        if (
            length < 0
            || schema[keyword] is JsonValue existingValue
                && existingValue.TryGetValue<int>(out var existingLength)
                && (isMinimum ? existingLength >= length : existingLength <= length)
        )
            return;

        schema[keyword] = length;
    }

    private static void AddNotSchema(JsonObject schema, JsonObject notSchema)
    {
        if (schema["not"] is not JsonNode existingNot)
        {
            schema["not"] = notSchema;
            return;
        }

        var allOf = schema["allOf"] as JsonArray ?? [];
        allOf.Add(new JsonObject { ["not"] = CloneJsonValue(existingNot) });
        schema.Remove("not");
        allOf.Add(new JsonObject { ["not"] = notSchema });
        schema["allOf"] = allOf;
    }

    private static void AddPattern(JsonObject schema, string pattern)
    {
        if (schema["pattern"] is not JsonValue existingPattern)
        {
            schema["pattern"] = pattern;
            return;
        }

        if (existingPattern.TryGetValue<string>(out var existing) && existing == pattern)
            return;

        var allOf = schema["allOf"] as JsonArray ?? [];
        allOf.Add(new JsonObject { ["pattern"] = pattern });
        schema["allOf"] = allOf;
    }

    private static bool IsJsonSchemaRegularExpression(string pattern)
    {
        // Unsupported .NET regex syntax is omitted rather than emitted as a misleading constraint.
        var index = 0;
        var inCharacterClass = false;
        while (index < pattern.Length)
        {
            if (
                !inCharacterClass
                && pattern[index] == '('
                && index + 1 < pattern.Length
                && pattern[index + 1] == '?'
                && (index + 2 >= pattern.Length || pattern[index + 2] is not (':' or '=' or '!'))
            )
                return false;

            if (pattern[index] != '\\')
            {
                if (
                    inCharacterClass
                    && pattern[index] == '-'
                    && index + 1 < pattern.Length
                    && pattern[index + 1] == '['
                )
                    return false;

                if (pattern[index] == '[')
                    inCharacterClass = true;
                else if (pattern[index] == ']')
                    inCharacterClass = false;
                index++;
                continue;
            }
            index++;
            if (index >= pattern.Length)
                return false;

            var escaped = pattern[index];
            if (escaped is 'x' or 'u')
            {
                var digitCount = escaped == 'x' ? 2 : 4;
                if (
                    index + digitCount >= pattern.Length
                    || !pattern.AsSpan(index + 1, digitCount).ToString().All(Uri.IsHexDigit)
                )
                    return false;
                index += digitCount;
                index++;
                continue;
            }

            if ("\\^$.*+?()[]{}|/-fnrtv".IndexOf(escaped) < 0)
                return false;
            index++;
        }

        return true;
    }

    private static void SetAllowedValues(JsonObject schema, object?[] values, JsonTypeInfo typeInfo)
    {
        var allowedValues = values.Select(value => CreateSchemaValue(value, typeInfo)).ToArray();
        if (schema["enum"] is JsonArray existingValues)
        {
            var intersection = new JsonArray(
                existingValues
                    .Where(existingValue =>
                        allowedValues.Any(allowedValue =>
                            AreJsonValuesEqual(existingValue, allowedValue)
                        )
                    )
                    .Select(CloneJsonValue)
                    .ToArray()
            );
            schema["enum"] = intersection;
            return;
        }

        schema["enum"] = new JsonArray(allowedValues);
    }

    private static void AddDeniedValues(JsonObject schema, object?[] values, JsonTypeInfo typeInfo)
    {
        var deniedValues = new JsonArray(
            values.Select(value => CreateSchemaValue(value, typeInfo)).ToArray()
        );
        var notSchema = new JsonObject { ["enum"] = deniedValues };

        if (schema["not"] is null)
        {
            schema["not"] = notSchema;
            return;
        }

        var allOf = schema["allOf"] as JsonArray ?? [];
        if (schema["not"] is JsonNode existingNot)
        {
            allOf.Add(new JsonObject { ["not"] = CloneJsonValue(existingNot) });
            schema.Remove("not");
        }
        allOf.Add(new JsonObject { ["not"] = notSchema });
        schema["allOf"] = allOf;
    }

    private static JsonNode? CreateSchemaValue(object? value, JsonTypeInfo typeInfo) =>
        value switch
        {
            null => null,
            Enum enumValue when enumValue.GetType() == typeInfo.Type =>
                JsonSerializer.SerializeToNode(enumValue, typeInfo),
            string stringValue => JsonValue.Create(stringValue),
            char character => JsonValue.Create(character.ToString()),
            bool boolean => JsonValue.Create(boolean),
            byte number => JsonValue.Create(number),
            sbyte number => JsonValue.Create(number),
            short number => JsonValue.Create(number),
            ushort number => JsonValue.Create(number),
            int number => JsonValue.Create(number),
            uint number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            ulong number => JsonValue.Create(number),
            float number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            _ => throw new InvalidOperationException(
                $"The allowed or denied value type '{value.GetType().FullName}' cannot be represented in JSON Schema."
            ),
        };

    private static bool AreJsonValuesEqual(JsonNode? left, JsonNode? right) =>
        left?.ToJsonString() == right?.ToJsonString();

    private static JsonNode? CloneJsonValue(JsonNode? value) =>
        value is null ? null : JsonNode.Parse(value.ToJsonString());

    private static bool TryGetNumericBound(object value, out decimal bound) =>
        decimal.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out bound
        );

    private static void SetNumericBound(
        JsonObject schema,
        string keyword,
        decimal bound,
        bool isMinimum
    )
    {
        if (
            schema[keyword] is JsonValue existingValue
            && decimal.TryParse(
                existingValue.ToJsonString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var existingBound
            )
            && (isMinimum ? existingBound >= bound : existingBound <= bound)
        )
            return;

        schema[keyword] = bound;
    }
}
