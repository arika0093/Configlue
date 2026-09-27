using System.Buffers;
using System.Globalization;
using System.Text;
using Configlue;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly YamlStateCodec<T> _inner;
    private readonly INamingConvention _namingConvention;
    private readonly string? _modelId;
    private readonly string _versionProperty;
    private readonly string[] _fallbackProperties;

    /// <summary>Creates a legacy YAML decoder using the supplied YamlDotNet naming convention.</summary>
    public ConfigurationWritableYamlStateCodec(
        INamingConvention? namingConvention = null,
        string? modelId = null,
        string versionProperty = "$version",
        IEnumerable<string>? fallbackVersionProperties = null
    )
    {
        // C.W's VYaml formatters use lower-camel member names by default.
        _namingConvention = namingConvention ?? CamelCaseNamingConvention.Instance;
        _inner = new YamlStateCodec<T>(_namingConvention);
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
        var content = StrictUtf8.GetString(source.ToArray());
        var stream = new YamlStream();
        using (var reader = new StringReader(content))
        {
            stream.Load(reader);
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
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
            var node = root.Children[versionKey];
            if (
                node is not YamlScalarNode scalar
                || !int.TryParse(
                    scalar.Value,
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
            root.Children.Remove(versionKey);
            return Serialize(stream);
        }

        // Configuration.Writable treats a mapping with no explicit marker as version 1.
        schema = new StateSchemaMetadata(_modelId, StateSchemaMetadata.InitialVersion);
        return source;
    }

    private YamlNode? FindKey(YamlMappingNode root, string propertyName)
    {
        var convertedName = _namingConvention.Apply(propertyName);
        foreach (var key in root.Children.Keys)
        {
            if (
                key is YamlScalarNode scalar
                && (
                    string.Equals(scalar.Value, propertyName, StringComparison.Ordinal)
                    || string.Equals(scalar.Value, convertedName, StringComparison.Ordinal)
                    || string.Equals(
                        scalar.Value,
                        char.ToLowerInvariant(propertyName[0]) + propertyName[1..],
                        StringComparison.Ordinal
                    )
                )
            )
            {
                return key;
            }
        }

        return null;
    }

    private static ReadOnlySequence<byte> Serialize(YamlStream stream)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer);
        return new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(writer.ToString()));
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
