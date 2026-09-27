using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Configlue;
using SharpYaml;
using SharpYaml.Serialization;

namespace Configlue.Provider.Yaml;

/// <summary>Decodes inline-versioned YAML written by Configuration.Writable.</summary>
/// <remarks>
/// This opt-in codec reads <c>$version</c> (or the legacy <c>Version</c> fallback) from the
/// selected YAML mapping, removes that property before decoding, and exposes its version through
/// <see cref="IStateSchemaMetadataReader"/>. Supply <c>modelId</c> to associate the
/// legacy version with a Configlue schema ID for historical schema dispatch. Serialization is
/// unsupported because this codec is intended only for reading the legacy representation.
/// </remarks>
public sealed class ConfigurationWritableYamlStateCodec<T>
    : IStateCodec<T>,
        IStateSchemaMetadataReader
{
    private readonly YamlStateCodec<T> _inner;
    private readonly JsonNamingPolicy _namingPolicy;
    private readonly YamlSerializerOptions _serializerOptions;
    private readonly Encoding _encoding;
    private readonly string? _modelId;
    private readonly string _versionProperty;
    private readonly string[] _fallbackProperties;

    /// <summary>Creates a legacy YAML decoder using the supplied property naming policy.</summary>
    public ConfigurationWritableYamlStateCodec(
        JsonNamingPolicy? namingPolicy = null,
        string? modelId = null,
        string versionProperty = "$version",
        IEnumerable<string>? fallbackVersionProperties = null,
        Encoding? encoding = null,
        ConfiglueModelSchema? modelSchema = null,
        YamlSerializerOptions? serializerOptions = null
    )
    {
        // C.W's VYaml formatters use lower-camel member names by default.
        _namingPolicy = namingPolicy ?? JsonNamingPolicy.CamelCase;
        var options = serializerOptions ?? YamlSerializerOptions.Default;
        _serializerOptions = options.TypeInfoResolver is YamlSerializerContext context
            ? context.CreateOptions(ConfigureOptions)
            : ConfigureOptions(options);

        YamlSerializerOptions ConfigureOptions(YamlSerializerOptions source) =>
            source with
            {
                PropertyNamingPolicy = _namingPolicy,
                DuplicateKeyHandling = YamlDuplicateKeyHandling.Error,
            };
        _encoding = encoding ?? new UTF8Encoding(false, true);
        _inner = new YamlStateCodec<T>(_namingPolicy, modelSchema, serializerOptions);
        _modelId = ValidateModelId(modelId);
        _versionProperty = ValidateVersionProperty(versionProperty, nameof(versionProperty));
        _fallbackProperties = ValidateFallbacks(fallbackVersionProperties);
    }

    /// <inheritdoc />
    public T? Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context)
    {
        var prepared = Prepare(source, out _);
        return _inner.Deserialize(in prepared, in context);
    }

    /// <inheritdoc />
    public void Serialize(
        T? value,
        IBufferWriter<byte> destination,
        in StateCodecContext context
    ) =>
        throw new NotSupportedException(
            "Configuration.Writable legacy YAML codecs are read-only. Use YamlStateCodec to write Configlue state."
        );

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source)
    {
        _ = Prepare(source, out var schema);
        return schema;
    }

    private ReadOnlySequence<byte> Prepare(
        in ReadOnlySequence<byte> source,
        out StateSchemaMetadata? schema
    )
    {
        var content = Decode(source.ToArray());
        if (string.IsNullOrWhiteSpace(content))
        {
            // C.W treats an empty YAML file as a default-constructed settings object.
            // An empty mapping produces the corresponding sparse fragment without inventing values.
            schema = null;
            return new ReadOnlySequence<byte>("{}"u8.ToArray());
        }

        var rootValue = YamlSerializer.Deserialize<Dictionary<string, object?>>(
            content,
            _serializerOptions
        );
        if (rootValue is not IDictionary<string, object?> root)
        {
            schema = null;
            return source;
        }

        var versionKey = FindKey(root, _versionProperty);
        if (versionKey is null)
        {
            foreach (var fallback in _fallbackProperties)
            {
                versionKey = FindKey(root, fallback);
                if (versionKey is not null)
                {
                    break;
                }
            }
        }

        if (versionKey is not null)
        {
            var node = root[versionKey];
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
                        Convert.ToString(node, CultureInfo.InvariantCulture),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var version
                    )
                    || version < StateSchemaMetadata.InitialVersion
                )
                {
                    throw new YamlException(
                        $"Configuration.Writable version property '{_versionProperty}' must be a positive integer."
                    );
                }

                schema = new StateSchemaMetadata(_modelId, version);
            }
            else
            {
                // C.W reads only scalar version nodes. Other YAML node kinds mean no marker,
                // so retain the legacy default of version 1 and strip the metadata key.
                schema = new StateSchemaMetadata(_modelId, StateSchemaMetadata.InitialVersion);
            }

            root.Remove(versionKey);
        }
        else
        {
            // Configuration.Writable treats a mapping with no explicit marker as version 1.
            schema = new StateSchemaMetadata(_modelId, StateSchemaMetadata.InitialVersion);
        }

        var schemaKey = FindKey(root, "$schema");
        if (schemaKey is null && versionKey is null)
        {
            return new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(content));
        }
        if (schemaKey is not null)
        {
            root.Remove(schemaKey);
        }

        return Serialize(root);
    }

    private string? FindKey(IDictionary<string, object?> root, string propertyName)
    {
        var convertedName = _namingPolicy.ConvertName(propertyName);
        return root.Keys.FirstOrDefault(key =>
            string.Equals(key, propertyName, StringComparison.Ordinal)
            || string.Equals(key, convertedName, StringComparison.Ordinal)
            || string.Equals(
                key,
                char.ToLowerInvariant(propertyName[0]) + propertyName[1..],
                StringComparison.Ordinal
            )
        );
    }

    private ReadOnlySequence<byte> Serialize(object value)
    {
        var yaml = YamlSerializer.Serialize(value, value.GetType(), _serializerOptions);
        return new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(yaml));
    }

    private string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(
            stream,
            _encoding,
            detectEncodingFromByteOrderMarks: true
        );
        return reader.ReadToEnd();
    }

    private static string? ValidateModelId(string? modelId)
    {
        if (modelId is not null && string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException(
                "A model ID must be non-empty when supplied.",
                nameof(modelId)
            );
        }

        return modelId;
    }

    private static string ValidateVersionProperty(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A version property cannot be empty.", parameterName);
        }
        return value;
    }

    private static string[] ValidateFallbacks(IEnumerable<string>? properties)
    {
        var fallbacks = properties?.ToArray() ?? ["Version"];
        if (fallbacks.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Fallback version property names cannot be empty.",
                nameof(properties)
            );
        }

        return fallbacks;
    }
}
