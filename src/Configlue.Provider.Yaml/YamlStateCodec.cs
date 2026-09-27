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
    private readonly DocumentLayoutOptions? _layout;
    private readonly Encoding? _textEncoding;

    /// <summary>Creates a codec using SharpYaml options and an optional generated model schema.</summary>
    public YamlStateCodec(
        JsonNamingPolicy? namingPolicy = null,
        ConfiglueModelSchema? modelSchema = null,
        YamlSerializerOptions? serializerOptions = null,
        DocumentLayoutOptions? documentLayout = null,
        Encoding? textEncoding = null
    )
    {
        _schema = modelSchema;
        _layout = documentLayout;
        _textEncoding = textEncoding;
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
        var yaml = YamlStateCodecOperations.GetPayload(
            source.ToArray(),
            _options,
            _layout,
            _namingPolicy,
            _textEncoding,
            out _
        );
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
        string? schemaReference = null;
        if (context.SchemaReferenceBaseUri is { } schemaReferenceBaseUri)
        {
            if (schemaMetadata is not { } referencedSchema)
            {
                throw new InvalidOperationException(
                    "A schema reference base URI requires schema metadata."
                );
            }

            schemaReference = StateSchemaReference.CreateUri(
                schemaReferenceBaseUri,
                referencedSchema
            );
        }
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
            yaml = WriteDocument(yaml, value, schema, metadata);
        }

        if (schemaReference is not null)
        {
            yaml = $"# yaml-language-server: $schema={schemaReference}{Environment.NewLine}{yaml}";
        }

        var bytes = Encoding.UTF8.GetBytes(yaml);
        bytes.CopyTo(destination.GetSpan(bytes.Length));
        destination.Advance(bytes.Length);
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        YamlStateCodecOperations.ReadSchemaMetadata(
            source.ToArray(),
            _options,
            _layout,
            _namingPolicy,
            _textEncoding
        );

    private string WriteDocument(
        string payloadYaml,
        object? value,
        ConfiglueModelSchema? schema,
        StateSchemaMetadata metadata
    )
    {
        if ((_layout?.Layout ?? DocumentLayout.Simple) == DocumentLayout.Simple)
        {
            var versionProperty = _layout?.VersionProperty ?? "$version";
            object? payloadNode = value is IConfiglueFragment fragment
                ? FragmentYamlConverterFactory.ToYamlValue(
                    fragment,
                    schema ?? fragment.Schema,
                    _namingPolicy
                )
                : YamlSerializer.Deserialize<Dictionary<string, object?>>(payloadYaml, _options);
            if (
                payloadNode is IDictionary<string, object?> mapping
                && !mapping.ContainsKey(versionProperty)
            )
            {
                var document = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [versionProperty] = metadata.Version,
                };
                foreach (var pair in mapping)
                {
                    document.Add(pair.Key, pair.Value);
                }

                return YamlSerializer.Serialize(document, document.GetType(), _options);
            }
        }

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
        return YamlSerializer.Serialize(envelope, envelope.GetType(), _options);
    }

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
        YamlSerializerOptions? serializerOptions = null,
        DocumentLayoutOptions? documentLayout = null,
        Encoding? textEncoding = null
    ) =>
        _inner = new YamlStateCodec(
            namingPolicy,
            modelSchema,
            serializerOptions,
            documentLayout,
            textEncoding
        );

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

internal static class YamlStateCodecOperations
{
    internal const string MetadataKey = "$configlue";
    internal const string PayloadKey = "$value";
    private const string SchemaProperty = "$schema";
    private const string DefaultVersionProperty = "$version";

    internal static object? GetPayload(
        byte[] content,
        YamlSerializerOptions options,
        DocumentLayoutOptions? layout,
        JsonNamingPolicy? namingPolicy,
        Encoding? textEncoding,
        out StateSchemaMetadata? schema
    )
    {
        var root = DeserializeRoot(content, options, textEncoding);
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

        schema = ReadSimpleVersion(root, layout, namingPolicy);
        if (root is not IDictionary<string, object?> simpleMapping || simpleMapping.Count == 0)
        {
            return root;
        }

        var versionKey = FindKey(simpleMapping, layout, namingPolicy);
        var schemaKey = FindSchemaKey(simpleMapping);
        if (versionKey is null && schemaKey is null)
        {
            return root;
        }

        var stripped = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in simpleMapping)
        {
            if (
                string.Equals(pair.Key, versionKey, StringComparison.Ordinal)
                || string.Equals(pair.Key, schemaKey, StringComparison.Ordinal)
            )
            {
                continue;
            }

            stripped.Add(pair.Key, pair.Value);
        }

        return stripped;
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
        YamlSerializerOptions options,
        DocumentLayoutOptions? layout,
        JsonNamingPolicy? namingPolicy,
        Encoding? textEncoding
    )
    {
        var root = DeserializeRoot(content, options, textEncoding);
        if (
            root is IDictionary<string, object?> mapping
            && mapping.TryGetValue(MetadataKey, out var metadataNode)
        )
        {
            return ReadMetadata(metadataNode);
        }

        return ReadSimpleVersion(root, layout, namingPolicy);
    }

    private static StateSchemaMetadata? ReadSimpleVersion(
        object? root,
        DocumentLayoutOptions? layout,
        JsonNamingPolicy? namingPolicy
    )
    {
        if (root is not IDictionary<string, object?> mapping || mapping.Count == 0)
        {
            return null;
        }

        var modelId = layout?.ModelId;
        var versionKey = FindKey(mapping, layout, namingPolicy);
        if (versionKey is null)
        {
            // A schema-annotated document without a version defaults to version 1. A document
            // without any marker keeps the legacy bare behavior, unless a model ID opts the
            // reader into legacy version attribution for migration.
            return FindSchemaKey(mapping) is not null || modelId is not null
                ? new StateSchemaMetadata(modelId, StateSchemaMetadata.InitialVersion)
                : null;
        }

        var node = mapping[versionKey];
        if (
            node is string
            || (
                node is not IDictionary<string, object?>
                && node is not System.Collections.IEnumerable
            )
        )
        {
            if (
                !int.TryParse(
                    Convert.ToString(node, System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var version
                )
                || version < StateSchemaMetadata.InitialVersion
            )
            {
                throw new YamlException(
                    $"The version property '{versionKey}' must be a positive integer."
                );
            }

            return new StateSchemaMetadata(modelId, version);
        }

        return new StateSchemaMetadata(modelId, StateSchemaMetadata.InitialVersion);
    }

    private static string? FindKey(
        IDictionary<string, object?> mapping,
        DocumentLayoutOptions? layout,
        JsonNamingPolicy? namingPolicy
    )
    {
        if (
            FindKey(mapping, layout?.VersionProperty ?? DefaultVersionProperty, namingPolicy) is
            { } versionKey
        )
        {
            return versionKey;
        }

        var fallbacks = layout?.FallbackVersionProperties ?? ["Version"];
        foreach (var fallback in fallbacks)
        {
            if (FindKey(mapping, fallback, namingPolicy) is { } fallbackKey)
            {
                return fallbackKey;
            }
        }

        return null;
    }

    private static string? FindKey(
        IDictionary<string, object?> mapping,
        string propertyName,
        JsonNamingPolicy? namingPolicy
    )
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new ArgumentException(
                "A version property cannot be empty.",
                nameof(propertyName)
            );
        }

        var convertedName = namingPolicy?.ConvertName(propertyName) ?? propertyName;
        return mapping.Keys.FirstOrDefault(key =>
            string.Equals(key, propertyName, StringComparison.Ordinal)
            || string.Equals(key, convertedName, StringComparison.Ordinal)
            || string.Equals(
                key,
                char.ToLowerInvariant(propertyName[0]) + propertyName[1..],
                StringComparison.Ordinal
            )
        );
    }

    private static string? FindSchemaKey(IDictionary<string, object?> mapping) =>
        mapping.Keys.FirstOrDefault(key =>
            string.Equals(key, SchemaProperty, StringComparison.Ordinal)
        );

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

    private static object? DeserializeRoot(
        byte[] content,
        YamlSerializerOptions options,
        Encoding? textEncoding
    )
    {
        var text = DecodeText(content, textEncoding);
        return string.IsNullOrWhiteSpace(text)
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : YamlSerializer.Deserialize<Dictionary<string, object?>>(text, options);
    }

    private static readonly Encoding[] BomEncodings =
    [
        // Longer preambles first: UTF-32 LE shares its first two bytes with UTF-16 LE.
        Encoding.UTF32,
        Encoding.GetEncoding(12001),
        Encoding.UTF8,
        Encoding.Unicode,
        Encoding.BigEndianUnicode,
    ];

    private static string DecodeText(byte[] content, Encoding? textEncoding)
    {
        if (textEncoding is not null)
        {
            using var stream = new MemoryStream(content, writable: false);
            using var reader = new StreamReader(
                stream,
                textEncoding,
                detectEncodingFromByteOrderMarks: true
            );
            return reader.ReadToEnd();
        }

        foreach (var encoding in BomEncodings)
        {
            var preamble = encoding.GetPreamble();
            if (
                preamble.Length > 0
                && content.Length >= preamble.Length
                && content.AsSpan(0, preamble.Length).SequenceEqual(preamble)
            )
            {
                return encoding.GetString(
                    content,
                    preamble.Length,
                    content.Length - preamble.Length
                );
            }
        }

        return new UTF8Encoding(false, true).GetString(content);
    }
}
