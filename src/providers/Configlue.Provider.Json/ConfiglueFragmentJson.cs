using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Configlue.Provider.Json;

/// <summary>
/// Non-generic JSON serialization for generated Configlue fragments over the State HTTP transport.
/// </summary>
/// <remarks>
/// Uses the generated fragment JSON converter registered by model code, so no reflection-based
/// model serialization is involved. Scalar and collection members still use
/// <see cref="JsonSerializerOptions.TypeInfoResolver"/> metadata; NativeAOT applications must
/// supply a source-generated resolver covering those member types.
/// </remarks>
public static class ConfiglueFragmentJson
{
    /// <summary>Creates JSON options ensuring a type-info resolver is present.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The reflection resolver is created only when reflection-based serialization is enabled. NativeAOT applications must supply a source-generated resolver, which bypasses this branch."
    )]
    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification = "The reflection resolver is created only when reflection-based serialization is enabled. NativeAOT applications must supply a source-generated resolver, which bypasses this branch."
    )]
    public static JsonSerializerOptions CreateOptions(JsonSerializerOptions? options = null)
    {
        var effective = options is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
        if (effective.TypeInfoResolver is not null)
        {
            return effective;
        }

        if (!JsonSerializer.IsReflectionEnabledByDefault)
        {
            throw new InvalidOperationException(
                "JSON fragment serialization requires JsonSerializerOptions.TypeInfoResolver when reflection is disabled. Supply a source-generated JsonSerializerContext covering the scalar/collection member types used by the generated fragment converter."
            );
        }

        effective.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        return effective;
    }

    /// <summary>Serializes a generated fragment to canonical JSON bytes.</summary>
    public static byte[] SerializeToCanonicalBytes(
        object fragment,
        Type fragmentType,
        JsonSerializerOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(fragmentType);
        var writer =
            ConfiglueJsonFragmentConverters.GetWriterOrNull(fragmentType)
            ?? throw new InvalidOperationException(
                $"No generated JSON converter is registered for fragment '{fragmentType}'."
            );
        var effective = CreateOptions(options);
        var buffer = new ArrayBufferWriter<byte>();
        using (var jsonWriter = new Utf8JsonWriter(buffer))
        {
            writer(jsonWriter, fragment, effective);
            jsonWriter.Flush();
        }

        return buffer.WrittenMemory.ToArray();
    }

    /// <summary>Deserializes canonical JSON bytes to a generated fragment.</summary>
    public static object Deserialize(
        Type fragmentType,
        ReadOnlySpan<byte> json,
        JsonSerializerOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(fragmentType);
        var readerDelegate =
            ConfiglueJsonFragmentConverters.GetReaderOrNull(fragmentType)
            ?? throw new InvalidOperationException(
                $"No generated JSON converter is registered for fragment '{fragmentType}'."
            );
        var effective = CreateOptions(options);
        var reader = new Utf8JsonReader(json);
        if (!reader.Read())
        {
            throw new JsonException("The JSON payload is empty.");
        }

        return readerDelegate(ref reader, effective);
    }

    /// <summary>Deserializes canonical JSON bytes to a generated fragment.</summary>
    public static object Deserialize(
        Type fragmentType,
        ReadOnlyMemory<byte> json,
        JsonSerializerOptions? options = null
    ) => Deserialize(fragmentType, json.Span, options);
}
