using System.Text;
using System.Text.Json;

namespace Configlue.Resource.Kubernetes;

/// <summary>Deterministic whole-object encoding shared by ConfigMap and Secret object mode.</summary>
/// <remarks>
/// Whole-object mode encodes the complete key set as sorted JSON so the same object always
/// produces the same bytes. Binary entries use standard base64. Consumers decode through the
/// canonical Resource + Codec path; no Codec logic is duplicated here.
/// </remarks>
internal static class KubernetesObjectEncoding
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    public static byte[] EncodeConfigMapObject(
        IReadOnlyDictionary<string, string> data,
        IReadOnlyDictionary<string, byte[]> binaryData
    )
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("data");
            foreach (
                var pair in new SortedDictionary<string, string>(
                    data.ToDictionary(static entry => entry.Key, static entry => entry.Value),
                    StringComparer.Ordinal
                )
            )
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteStartObject("binaryData");
            foreach (
                var pair in new SortedDictionary<string, byte[]>(
                    binaryData.ToDictionary(static entry => entry.Key, static entry => entry.Value),
                    StringComparer.Ordinal
                )
            )
            {
                writer.WriteString(pair.Key, Convert.ToBase64String(pair.Value));
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static byte[] EncodeSecretObject(IReadOnlyDictionary<string, byte[]> data)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("data");
            foreach (
                var pair in new SortedDictionary<string, byte[]>(
                    data.ToDictionary(static entry => entry.Key, static entry => entry.Value),
                    StringComparer.Ordinal
                )
            )
            {
                writer.WriteString(pair.Key, Convert.ToBase64String(pair.Value));
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static void DecodeConfigMapObject(
        ReadOnlyMemory<byte> content,
        out Dictionary<string, string> data,
        out Dictionary<string, byte[]> binaryData
    )
    {
        data = new Dictionary<string, string>(StringComparer.Ordinal);
        binaryData = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(content.Span));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "A Kubernetes ConfigMap object payload must be a JSON object."
            );
        }

        if (
            document.RootElement.TryGetProperty("data", out var dataElement)
            && dataElement.ValueKind == JsonValueKind.Object
        )
        {
            foreach (var property in dataElement.EnumerateObject())
            {
                data[property.Name] =
                    property.Value.GetString()
                    ?? throw new InvalidDataException(
                        "A Kubernetes ConfigMap data entry must be a string."
                    );
            }
        }

        if (
            document.RootElement.TryGetProperty("binaryData", out var binaryElement)
            && binaryElement.ValueKind == JsonValueKind.Object
        )
        {
            foreach (var property in binaryElement.EnumerateObject())
            {
                var text =
                    property.Value.GetString()
                    ?? throw new InvalidDataException(
                        "A Kubernetes binaryData entry must be base64."
                    );
                try
                {
                    binaryData[property.Name] = Convert.FromBase64String(text);
                }
                catch (FormatException exception)
                {
                    throw new InvalidDataException(
                        "A Kubernetes binaryData entry must be base64.",
                        exception
                    );
                }
            }
        }
    }

    public static Dictionary<string, byte[]> DecodeSecretObject(ReadOnlyMemory<byte> content)
    {
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(content.Span));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "A Kubernetes Secret object payload must be a JSON object."
            );
        }

        var payload = document.RootElement;
        if (payload.TryGetProperty("data", out var dataElement))
        {
            payload = dataElement;
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "A Kubernetes Secret object payload must be a JSON object."
            );
        }

        foreach (var property in payload.EnumerateObject())
        {
            var text =
                property.Value.GetString()
                ?? throw new InvalidDataException("A Kubernetes Secret entry must be base64.");
            try
            {
                data[property.Name] = Convert.FromBase64String(text);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "A Kubernetes Secret entry must be base64.",
                    exception
                );
            }
        }

        return data;
    }
}
