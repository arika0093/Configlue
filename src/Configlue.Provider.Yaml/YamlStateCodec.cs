using System.Buffers;
using System.Text;
using Configlue;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Configlue.Provider.Yaml;

/// <summary>A YAML codec for ordinary models and generated sparse fragments.</summary>
public sealed class YamlStateCodec
    : IStateCodec,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    /// <summary>Creates a YAML codec, optionally applying a YamlDotNet naming convention.</summary>
    public YamlStateCodec(INamingConvention? namingConvention = null)
    {
        var convention = namingConvention ?? NullNamingConvention.Instance;
        var converter = new FragmentYamlTypeConverter(convention);
        var collections = new CollectionInterfaceYamlTypeConverter();
        var serializerBuilder = new SerializerBuilder()
            .WithTypeConverter(converter)
            .WithTypeConverter(collections);
        var deserializerBuilder = new DeserializerBuilder()
            .WithTypeConverter(converter)
            .WithTypeConverter(collections);
        if (namingConvention is not null)
        {
            serializerBuilder.WithNamingConvention(namingConvention);
            deserializerBuilder.WithNamingConvention(namingConvention);
        }

        _serializer = serializerBuilder.Build();
        _deserializer = deserializerBuilder.Build();
    }

    /// <inheritdoc />
    public object? Deserialize(
        Type type,
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        var yaml = YamlStateCodecOperations.GetPayload(source.ToArray(), out _);
        return _deserializer.Deserialize(new StringReader(yaml), type);
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
        var payload = _serializer.Serialize(value, type);
        var schema =
            context.Schema
            ?? (value is IConfiglueFragment fragment ? fragment.Schema.ToMetadata() : null);
        var yaml = schema is { } metadata
            ? YamlStateCodecOperations.WriteEnvelope(metadata, payload)
            : payload;
        var bytes = Encoding.UTF8.GetBytes(yaml);
        bytes.CopyTo(destination.GetSpan(bytes.Length));
        destination.Advance(bytes.Length);
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        YamlStateCodecOperations.ReadSchemaMetadata(source.ToArray());

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) =>
        exception is YamlException or DecoderFallbackException;
}

/// <summary>A typed YAML state codec fast path.</summary>
public sealed class YamlStateCodec<T>
    : IStateCodec<T>,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    private readonly YamlStateCodec _inner;

    /// <summary>Creates a typed YAML codec, optionally applying a YamlDotNet naming convention.</summary>
    public YamlStateCodec(INamingConvention? namingConvention = null) =>
        _inner = new YamlStateCodec(namingConvention);

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

internal sealed class FragmentYamlTypeConverter(INamingConvention namingConvention)
    : IYamlTypeConverter
{
    public bool Accepts(Type type) => typeof(IConfiglueFragment).IsAssignableFrom(type);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (!parser.TryConsume<MappingStart>(out _))
        {
            throw new YamlException("A Configlue fragment must be a YAML mapping.");
        }

        var emptyProperty =
            type.GetProperty(
                "Empty",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
            ) ?? throw new YamlException($"Generated fragment '{type}' has no Empty value.");
        var fragment =
            (IConfiglueFragment?)emptyProperty.GetValue(null)
            ?? throw new YamlException($"Generated fragment '{type}' returned a null Empty value.");
        var membersByName = fragment.Schema.Members.ToDictionary(
            member => namingConvention.Apply(member.Name),
            StringComparer.Ordinal
        );
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            if (!parser.TryConsume<Scalar>(out var key))
            {
                throw new YamlException("A Configlue fragment mapping must use scalar keys.");
            }

            if (!seenNames.Add(key.Value))
            {
                throw new YamlException($"Duplicate Configlue fragment member '{key.Value}'.");
            }

            if (membersByName.TryGetValue(key.Value, out var member))
            {
                var value = rootDeserializer(
                    CollectionInterfaceYamlTypeConverter.GetConcreteType(member.ValueType)
                );
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
            else
            {
                _ = rootDeserializer(typeof(object));
            }
        }

        return fragment;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is not IConfiglueFragment fragment)
        {
            throw new YamlException(
                $"Value for generated fragment type '{type}' does not implement {nameof(IConfiglueFragment)}."
            );
        }

        emitter.Emit(new MappingStart());
        foreach (var present in fragment.EnumeratePresentMembers())
        {
            var member = fragment.Schema.Members.First(candidate => candidate.Id == present.Id);
            emitter.Emit(new Scalar(namingConvention.Apply(member.Name)));
            serializer(present.Value, member.ValueType);
        }

        emitter.Emit(new MappingEnd());
    }
}

internal sealed class CollectionInterfaceYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => GetConcreteType(type) != type;

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) =>
        rootDeserializer(GetConcreteType(type));

    public void WriteYaml(
        IEmitter emitter,
        object? value,
        Type type,
        ObjectSerializer serializer
    ) => serializer(value, GetConcreteType(type));

    internal static Type GetConcreteType(Type type)
    {
        if (!type.IsInterface || !type.IsGenericType)
        {
            return type;
        }

        var definition = type.GetGenericTypeDefinition();
        var elementType = type.GetGenericArguments()[0];
        if (definition == typeof(ISet<>) || definition == typeof(IReadOnlySet<>))
        {
            return typeof(HashSet<>).MakeGenericType(elementType);
        }

        if (
            definition == typeof(IEnumerable<>)
            || definition == typeof(ICollection<>)
            || definition == typeof(IReadOnlyCollection<>)
            || definition == typeof(IList<>)
            || definition == typeof(IReadOnlyList<>)
        )
        {
            return typeof(List<>).MakeGenericType(elementType);
        }

        return type;
    }
}

internal static class YamlStateCodecOperations
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private const string MetadataKey = "$configlue";
    private const string PayloadKey = "$value";

    public static string GetPayload(byte[] content, out StateSchemaMetadata? schema)
    {
        var root = ReadRoot(content);
        if (
            root is YamlMappingNode mapping
            && mapping.Children.TryGetValue(new YamlScalarNode(MetadataKey), out var metadataNode)
        )
        {
            schema = ReadMetadata(metadataNode);
            if (!mapping.Children.TryGetValue(new YamlScalarNode(PayloadKey), out var payloadNode))
            {
                throw new YamlException(
                    $"The '{MetadataKey}' metadata envelope must contain a '{PayloadKey}' value."
                );
            }

            return WriteNode(payloadNode);
        }

        schema = null;
        return WriteNode(root);
    }

    public static string WriteEnvelope(StateSchemaMetadata schema, string payload)
    {
        if (schema.Version < StateSchemaMetadata.InitialVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schema),
                "Schema versions must be positive."
            );
        }

        var valueNode = ReadRoot(Encoding.UTF8.GetBytes(payload));
        var schemaNode = new YamlMappingNode
        {
            {
                "version",
                schema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
        };
        if (schema.ModelId is not null)
        {
            schemaNode.Add("id", schema.ModelId);
        }

        var root = new YamlMappingNode { { MetadataKey, schemaNode }, { PayloadKey, valueNode } };
        return WriteNode(root);
    }

    public static StateSchemaMetadata? ReadSchemaMetadata(byte[] content)
    {
        var root = ReadRoot(content);
        return
            root is YamlMappingNode mapping
            && mapping.Children.TryGetValue(new YamlScalarNode(MetadataKey), out var metadataNode)
            ? ReadMetadata(metadataNode)
            : null;
    }

    private static StateSchemaMetadata ReadMetadata(YamlNode node)
    {
        if (
            node is not YamlMappingNode metadata
            || !metadata.Children.TryGetValue(new YamlScalarNode("version"), out var versionNode)
            || versionNode is not YamlScalarNode versionScalar
            || !int.TryParse(
                versionScalar.Value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var version
            )
            || version < StateSchemaMetadata.InitialVersion
        )
        {
            throw new YamlException(
                $"The '{MetadataKey}' metadata must contain a positive integer version."
            );
        }

        var id =
            metadata.Children.TryGetValue(new YamlScalarNode("id"), out var idNode)
            && idNode is YamlScalarNode idScalar
                ? idScalar.Value
                : null;
        return new StateSchemaMetadata(id, version);
    }

    private static YamlNode ReadRoot(byte[] content)
    {
        using var reader = new StringReader(StrictUtf8.GetString(content));
        var stream = new YamlStream();
        stream.Load(reader);
        if (stream.Documents.Count != 1)
        {
            throw new YamlException("A Configlue YAML resource must contain exactly one document.");
        }

        return stream.Documents[0].RootNode;
    }

    private static string WriteNode(YamlNode node)
    {
        var stream = new YamlStream(new YamlDocument(node));
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        stream.Save(writer);
        return writer.ToString();
    }
}
