using System.Buffers;
using System.Text;
using System.Text.Json;
using Configlue;
using SharpYaml;
using SharpYaml.Serialization;

namespace Configlue.Provider.Yaml;

/// <summary>A YAML codec for ordinary models and generated sparse fragments.</summary>
public sealed class YamlStateCodec
    : IStateCodec,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    private readonly YamlSerializerOptions _options;
    private readonly ConfiglueModelSchema? _schema;
    private readonly JsonNamingPolicy? _namingPolicy;

    /// <summary>Creates a codec using SharpYaml options and an optional generated model schema.</summary>
    public YamlStateCodec(
        JsonNamingPolicy? namingPolicy = null,
        ConfiglueModelSchema? modelSchema = null,
        YamlSerializerOptions? serializerOptions = null
    )
    {
        _schema = modelSchema;
        var options = serializerOptions ?? YamlSerializerOptions.Default;
        _namingPolicy = namingPolicy ?? options.PropertyNamingPolicy;
        _options = options.TypeInfoResolver is YamlSerializerContext context
            ? context.CreateOptions(ConfigureOptions)
            : ConfigureOptions(options);

        YamlSerializerOptions ConfigureOptions(YamlSerializerOptions source)
        {
            var converters = new List<YamlConverter>(source.Converters.Count + 1)
            {
                new FragmentYamlConverterFactory(modelSchema, _namingPolicy),
            };
            converters.AddRange(source.Converters);
            return source with
            {
                PropertyNamingPolicy = _namingPolicy,
                DuplicateKeyHandling = YamlDuplicateKeyHandling.Error,
                Converters = converters,
            };
        }
    }

    /// <inheritdoc />
    public object? Deserialize(
        Type type,
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        var yaml = YamlStateCodecOperations.GetPayload(source.ToArray(), _options, out _);
        if (yaml is null)
        {
            return null;
        }

        var payload = YamlSerializer.Serialize(yaml, yaml.GetType(), _options);
        return YamlSerializer.Deserialize(payload, type, _options);
    }

    /// <inheritdoc />
    public void Serialize(
        Type type,
        object? value,
        IBufferWriter<byte> destination,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(destination);
        var schema = GetSchema(value, _schema);
        var schemaMetadata = context.Schema ?? schema?.ToMetadata();
        string yaml;
        if (value is IConfiglueFragment fragment)
        {
            var payload = FragmentYamlConverterFactory.ToYamlValue(
                fragment,
                schema ?? fragment.Schema,
                _namingPolicy
            );
            yaml = YamlSerializer.Serialize(payload, payload.GetType(), _options);
        }
        else
        {
            yaml = YamlSerializer.Serialize(value, type, _options);
        }

        if (schemaMetadata is { } metadata)
        {
            var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [YamlStateCodecOperations.MetadataKey] = YamlStateCodecOperations.WriteMetadata(
                    metadata
                ),
                [YamlStateCodecOperations.PayloadKey] = value is IConfiglueFragment sparseFragment
                    ? FragmentYamlConverterFactory.ToYamlValue(
                        sparseFragment,
                        schema ?? sparseFragment.Schema,
                        _namingPolicy
                    )
                    : value,
            };
            yaml = YamlSerializer.Serialize(envelope, envelope.GetType(), _options);
        }

        var bytes = Encoding.UTF8.GetBytes(yaml);
        bytes.CopyTo(destination.GetSpan(bytes.Length));
        destination.Advance(bytes.Length);
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        YamlStateCodecOperations.ReadSchemaMetadata(source.ToArray(), _options);

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) =>
        exception is YamlException or DecoderFallbackException;

    private static ConfiglueModelSchema? GetSchema(
        object? value,
        ConfiglueModelSchema? configuredSchema
    ) => value is IConfiglueFragment fragment ? fragment.Schema : configuredSchema;
}

/// <summary>A typed YAML state codec fast path.</summary>
public sealed class YamlStateCodec<T>
    : IStateCodec<T>,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    private readonly YamlStateCodec _inner;

    /// <summary>Creates a typed YAML codec with optional naming, schema, and SharpYaml options.</summary>
    public YamlStateCodec(
        JsonNamingPolicy? namingPolicy = null,
        ConfiglueModelSchema? modelSchema = null,
        YamlSerializerOptions? serializerOptions = null
    ) => _inner = new YamlStateCodec(namingPolicy, modelSchema, serializerOptions);

    /// <inheritdoc />
    public T? Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context) =>
        (T?)_inner.Deserialize(typeof(T), source, in context);

    /// <inheritdoc />
    public void Serialize(
        T? value,
        IBufferWriter<byte> destination,
        in StateCodecContext context
    ) => _inner.Serialize(typeof(T), value, destination, in context);

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        _inner.ReadSchemaMetadata(in source);

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) =>
        _inner.IsRecoverableReadException(exception);
}

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

internal static class YamlStateCodecOperations
{
    internal const string MetadataKey = "$configlue";
    internal const string PayloadKey = "$value";

    internal static object? GetPayload(
        byte[] content,
        YamlSerializerOptions options,
        out StateSchemaMetadata? schema
    )
    {
        var root = DeserializeRoot(content, options);
        if (
            root is IDictionary<string, object?> mapping
            && mapping.TryGetValue(MetadataKey, out var metadataNode)
        )
        {
            schema = ReadMetadata(metadataNode);
            if (!mapping.TryGetValue(PayloadKey, out var payload))
            {
                throw new YamlException(
                    $"The '{MetadataKey}' metadata envelope must contain a '{PayloadKey}' value."
                );
            }

            return payload;
        }

        schema = null;
        return root;
    }

    internal static Dictionary<string, object?> WriteMetadata(StateSchemaMetadata schema)
    {
        if (schema.Version < StateSchemaMetadata.InitialVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schema),
                "Schema versions must be positive."
            );
        }

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = schema.Version,
        };
        if (schema.ModelId is not null)
        {
            metadata.Add("id", schema.ModelId);
        }

        return metadata;
    }

    internal static StateSchemaMetadata? ReadSchemaMetadata(
        byte[] content,
        YamlSerializerOptions options
    )
    {
        var root = DeserializeRoot(content, options);
        return
            root is IDictionary<string, object?> mapping
            && mapping.TryGetValue(MetadataKey, out var metadataNode)
            ? ReadMetadata(metadataNode)
            : null;
    }

    private static StateSchemaMetadata ReadMetadata(object? node)
    {
        if (
            node is not IDictionary<string, object?> metadata
            || !metadata.TryGetValue("version", out var versionNode)
            || !TryReadVersion(versionNode, out var version)
        )
        {
            throw new YamlException(
                $"The '{MetadataKey}' metadata must contain a positive integer version."
            );
        }

        var id = metadata.TryGetValue("id", out var idNode) ? idNode as string : null;
        return new StateSchemaMetadata(id, version);
    }

    private static bool TryReadVersion(object? value, out int version)
    {
        switch (value)
        {
            case int parsed:
                version = parsed;
                break;
            case long parsed when parsed is >= int.MinValue and <= int.MaxValue:
                version = (int)parsed;
                break;
            case string text
                when int.TryParse(
                    text,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed
                ):
                version = parsed;
                break;
            default:
                version = 0;
                break;
        }

        return version >= StateSchemaMetadata.InitialVersion;
    }

    private static object? DeserializeRoot(byte[] content, YamlSerializerOptions options)
    {
        var text = new UTF8Encoding(false, true).GetString(content);
        return string.IsNullOrWhiteSpace(text)
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : YamlSerializer.Deserialize<Dictionary<string, object?>>(text, options);
    }
}
