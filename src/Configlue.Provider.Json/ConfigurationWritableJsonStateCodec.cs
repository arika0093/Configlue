using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Configlue.Provider.Json;

/// <summary>Decodes inline-versioned JSON written by Configuration.Writable.</summary>
/// <remarks>
/// This opt-in codec reads <c>$version</c> (or the legacy <c>Version</c> fallback) from the
/// selected JSON object, removes that property before decoding, and exposes its version through
/// <see cref="IStateSchemaMetadataReader"/>. Supply <c>modelId</c> to associate the
/// legacy version with a Configlue schema ID for historical schema dispatch. Serialization is
/// unsupported because this codec is intended only for reading the legacy representation.
/// </remarks>
public sealed class ConfigurationWritableJsonStateCodec<T>
    : IStateCodec<T>,
        IStateSchemaMetadataReader
{
    private readonly JsonStateCodec<T> _inner;
    private readonly JsonSerializerOptions _options;
    private readonly string? _modelId;
    private readonly string _versionProperty;
    private readonly string[] _fallbackProperties;

    /// <summary>Creates a reflection-based legacy decoder using the supplied JSON options.</summary>
    [RequiresUnreferencedCode(
        "Reflection-based JSON serialization may require types that trimming removes. Use the JsonTypeInfo constructor for trim-safe serialization."
    )]
    [RequiresDynamicCode(
        "Reflection-based JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT."
    )]
    public ConfigurationWritableJsonStateCodec(
        JsonSerializerOptions? options = null,
        string? modelId = null,
        string versionProperty = "$version",
        IEnumerable<string>? fallbackVersionProperties = null
    )
    {
        _options = options is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
        _inner = new JsonStateCodec<T>(_options);
        _modelId = ValidateModelId(modelId);
        _versionProperty = ValidateVersionProperty(versionProperty, nameof(versionProperty));
        _fallbackProperties = ValidateFallbacks(fallbackVersionProperties);
    }

    /// <summary>Creates a source-generated or otherwise preconfigured legacy JSON decoder.</summary>
    public ConfigurationWritableJsonStateCodec(
        JsonTypeInfo<T> typeInfo,
        string? modelId = null,
        string versionProperty = "$version",
        IEnumerable<string>? fallbackVersionProperties = null
    )
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        _options = typeInfo.Options;
        _inner = new JsonStateCodec<T>(typeInfo);
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
            "Configuration.Writable legacy JSON codecs are read-only. Use JsonStateCodec to write Configlue state."
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
        using var document = JsonDocument.Parse(source);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            schema = null;
            return source;
        }

        var root = document.RootElement;
        var versionName = FindProperty(root, _versionProperty);
        JsonElement versionValue = default;
        if (versionName is null)
        {
            foreach (var fallback in _fallbackProperties)
            {
                versionName = FindProperty(root, fallback);
                if (versionName is not null)
                {
                    break;
                }
            }
        }

        if (versionName is not null)
        {
            if (!root.TryGetProperty(versionName, out versionValue))
            {
                throw new JsonException(
                    $"Configuration.Writable version property '{versionName}' was not found."
                );
            }

            if (
                versionValue.ValueKind != JsonValueKind.Number
                || !versionValue.TryGetInt32(out var version)
                || version < StateSchemaMetadata.InitialVersion
            )
            {
                throw new JsonException(
                    $"Configuration.Writable version property '{versionName}' must be a positive integer."
                );
            }

            schema = new StateSchemaMetadata(_modelId, version);
        }
        else
        {
            // Configuration.Writable treats an object with no explicit marker as version 1.
            schema = new StateSchemaMetadata(_modelId, StateSchemaMetadata.InitialVersion);
        }

        var hasSchemaReference = root.TryGetProperty("$schema", out _);
        if (versionName is null && !hasSchemaReference)
        {
            return source;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (
                var property in root.EnumerateObject()
                    .Where(property =>
                        !string.Equals(property.Name, versionName, StringComparison.Ordinal)
                        && !string.Equals(property.Name, "$schema", StringComparison.Ordinal)
                    )
            )
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return new ReadOnlySequence<byte>(buffer.WrittenMemory);
    }

    private string? FindProperty(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out _))
        {
            return propertyName;
        }

        var convertedName = _options.PropertyNamingPolicy?.ConvertName(propertyName);
        if (
            convertedName is not null
            && !string.Equals(convertedName, propertyName, StringComparison.Ordinal)
            && root.TryGetProperty(convertedName, out _)
        )
        {
            return convertedName;
        }

        if (_options.PropertyNameCaseInsensitive)
        {
            return root.EnumerateObject()
                .Select(property => property.Name)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate, propertyName, StringComparison.OrdinalIgnoreCase)
                    || (
                        convertedName is not null
                        && string.Equals(
                            candidate,
                            convertedName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                );
        }

        return null;
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
