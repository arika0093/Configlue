using System.Text.Json;
using Configlue;
using SharpYaml;
using SharpYaml.Serialization;

namespace Configlue.Provider.Yaml;

internal sealed class FragmentYamlConverterFactory(
    ConfiglueModelSchema? rootSchema,
    JsonNamingPolicy? namingPolicy
) : YamlConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeof(IConfiglueFragment).IsAssignableFrom(typeToConvert);

    public override YamlConverter CreateConverter(
        Type typeToConvert,
        YamlSerializerOptions options
    ) =>
        new FragmentYamlConverter(
            typeToConvert,
            FindSchema(typeToConvert, rootSchema),
            options,
            namingPolicy ?? options.PropertyNamingPolicy
        );

    private static ConfiglueModelSchema? FindSchema(Type type, ConfiglueModelSchema? rootSchema)
    {
        if (rootSchema is not null)
        {
            var schema = FindSchema(type, rootSchema, new HashSet<string>(StringComparer.Ordinal));
            if (schema is not null)
            {
                return schema;
            }
        }

        throw new YamlException(
            $"Generated fragment '{type}' cannot be resolved without its ConfiglueModelSchema. Pass the generated model schema to the codec."
        );
    }

    private static ConfiglueModelSchema? FindSchema(
        Type fragmentType,
        ConfiglueModelSchema schema,
        HashSet<string> visited
    )
    {
        if (!visited.Add(schema.Id))
        {
            return null;
        }

        if (schema.CreateEmptyFragment().GetType() == fragmentType)
        {
            return schema;
        }

        foreach (var member in schema.Members)
        {
            if (member.NestedSchemaFactory?.Invoke() is { } nested)
            {
                var match = FindSchema(fragmentType, nested, visited);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    internal static object ToYamlValue(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        JsonNamingPolicy? namingPolicy
    )
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var present in fragment.EnumeratePresentMembers())
        {
            var member = schema.Members.First(candidate => candidate.Id == present.Id);
            var name = namingPolicy?.ConvertName(member.Name) ?? member.Name;
            result.Add(name, ConvertValue(present.Value, member, namingPolicy));
        }

        return result;
    }

    private static object? ConvertValue(
        object? value,
        ConfiglueMemberSchema member,
        JsonNamingPolicy? namingPolicy
    )
    {
        if (value is not IConfiglueFragment child || member.NestedSchemaFactory is null)
        {
            if (value is System.Collections.IEnumerable sequence && value is not string)
            {
                var items = new List<object?>();
                foreach (var item in sequence)
                {
                    items.Add(item);
                }

                return items;
            }

            return value;
        }

        return ToYamlValue(child, member.NestedSchemaFactory(), namingPolicy);
    }
}

internal sealed class FragmentYamlConverter(
    Type fragmentType,
    ConfiglueModelSchema? schema,
    YamlSerializerOptions options,
    JsonNamingPolicy? namingPolicy
) : YamlConverter
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == fragmentType && typeof(IConfiglueFragment).IsAssignableFrom(typeToConvert);

    public override object Read(YamlReader reader, Type typeToConvert)
    {
        var node = YamlSerializer.Deserialize<Dictionary<string, object?>>(
            YamlReader.BufferCurrentNodeToString(reader),
            options
        );
        var fragmentSchema =
            schema
            ?? throw new YamlException(
                $"Generated fragment '{typeToConvert}' cannot be resolved without its ConfiglueModelSchema. Pass the generated model schema to the codec."
            );
        if (node is not IDictionary<string, object?> mapping)
        {
            throw new YamlException("A Configlue fragment must be a YAML mapping.");
        }

        var fragment = fragmentSchema.CreateEmptyFragment();
        var membersByName = fragmentSchema.Members.ToDictionary(
            member => namingPolicy?.ConvertName(member.Name) ?? member.Name,
            StringComparer.Ordinal
        );
        foreach (var pair in mapping)
        {
            if (!membersByName.TryGetValue(pair.Key, out var member))
            {
                continue;
            }

            var value = ConvertMember(pair.Value, member);
            if (
                value is null
                && member.ValueType.IsValueType
                && Nullable.GetUnderlyingType(member.ValueType) is null
            )
            {
                throw new YamlException($"Non-nullable member '{member.Name}' cannot be null.");
            }

            fragment = fragment.WithMember(member.Id, value);
        }

        return fragment;
    }

    public override void Write(YamlWriter writer, object? value)
    {
        if (value is not IConfiglueFragment fragment)
        {
            throw new YamlException(
                $"Value for generated fragment type '{fragmentType}' does not implement {nameof(IConfiglueFragment)}."
            );
        }

        var fragmentSchema =
            schema
            ?? throw new YamlException(
                $"Generated fragment '{fragmentType}' cannot be resolved without its ConfiglueModelSchema. Pass the generated model schema to the codec."
            );
        WriteObject(
            writer,
            FragmentYamlConverterFactory.ToYamlValue(fragment, fragmentSchema, namingPolicy)
        );
    }

    private object? ConvertMember(object? value, ConfiglueMemberSchema member)
    {
        if (value is null)
        {
            return null;
        }

        if (member.NestedSchemaFactory is { } nestedSchemaFactory)
        {
            var nestedSchema = nestedSchemaFactory();
            var nestedFragmentType = nestedSchema.CreateEmptyFragment().GetType();
            var yaml = YamlSerializer.Serialize(value, value.GetType(), options);
            return YamlSerializer.Deserialize(yaml, nestedFragmentType, options);
        }

        if (member.ValueType.IsInstanceOfType(value))
        {
            return value;
        }

        var dynamicOptions = YamlSerializerOptions.Default;
        var normalizedValue = value is System.Collections.IEnumerable sequence and not string
            ? sequence.Cast<object?>().ToList()
            : value;
        var valueNode = SharpYaml.Model.YamlNode.FromObject(
            normalizedValue,
            dynamicOptions,
            normalizedValue.GetType()
        );
        return valueNode.ToObject(member.ValueType, options);
    }

    private void WriteObject(YamlWriter writer, object? value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value is IDictionary<string, object?> mapping)
        {
            writer.WriteStartMapping();
            foreach (var pair in mapping)
            {
                writer.WritePropertyName(pair.Key);
                WriteObject(writer, pair.Value);
            }

            writer.WriteEndMapping();
            return;
        }

        if (value is System.Collections.IEnumerable sequence && value is not string)
        {
            writer.WriteStartSequence();
            foreach (var item in sequence)
            {
                WriteObject(writer, item);
            }

            writer.WriteEndSequence();
            return;
        }

        writer.GetConverter(value.GetType()).Write(writer, value);
    }
}
