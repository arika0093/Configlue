using System.Buffers;
using System.Text;
using MessagePack;
using MessagePack.Formatters;

namespace Configlue.Provider.MessagePack;

/// <summary>Reads and writes the versioned Configlue MessagePack envelope and its sparse payload.</summary>
/// <remarks>
/// A stored document is a two-entry map: <c>"$configlue"</c> carries the schema identifier and
/// version, and <c>"$value"</c> carries the payload. The payload for a generated fragment is a
/// sparse map keyed by ordinal case-sensitive CLR member names, so the binary shape never depends on
/// declaration or property order.
/// </remarks>
internal static class MessagePackStateCodecOperations
{
    internal const string MetadataProperty = "$configlue";
    internal const string PayloadProperty = "$value";
    internal const string VersionProperty = "version";
    internal const string IdProperty = "id";

    private static readonly byte[] MetadataPropertyUtf8 = Encoding.UTF8.GetBytes(MetadataProperty);
    private static readonly byte[] PayloadPropertyUtf8 = Encoding.UTF8.GetBytes(PayloadProperty);

    internal static void WriteEnvelope<T>(
        ref MessagePackWriter writer,
        T? value,
        StateSchemaMetadata? schema,
        MessagePackSerializerOptions options,
        IMessagePackFormatter<T> formatter
    )
    {
        writer.WriteMapHeader(2);
        writer.Write(MetadataProperty);
        WriteSchema(ref writer, schema);
        writer.Write(PayloadProperty);
        formatter.Serialize(ref writer, value!, options);
    }

    internal static void WriteEnvelope(
        ref MessagePackWriter writer,
        Type type,
        object? value,
        StateSchemaMetadata? schema,
        MessagePackSerializerOptions options
    )
    {
        writer.WriteMapHeader(2);
        writer.Write(MetadataProperty);
        WriteSchema(ref writer, schema);
        writer.Write(PayloadProperty);
        MessagePackSerializer.Serialize(type, ref writer, value, options);
    }

    internal static void WritePayload<T>(
        ref MessagePackWriter writer,
        T? value,
        MessagePackSerializerOptions options,
        IMessagePackFormatter<T>? formatter
    )
    {
        if (formatter is not null)
        {
            formatter.Serialize(ref writer, value!, options);
            return;
        }

        MessagePackSerializer.Serialize(ref writer, value, options);
    }

    internal static T? ReadValue<T>(
        in ReadOnlySequence<byte> source,
        MessagePackSerializerOptions options,
        IMessagePackFormatter<T> formatter
    )
    {
        var reader = new MessagePackReader(source);
        if (reader.TryReadNil())
        {
            return default;
        }

        if (!TryReadEnvelopeHeader(reader.CreatePeekReader()))
        {
            return ReadWithFormatter(ref reader, options, formatter);
        }

        var count = reader.ReadMapHeader();
        T? value = default;
        var found = false;
        for (var index = 0; index < count; index++)
        {
            var key = reader.ReadString();
            if (string.Equals(key, PayloadProperty, StringComparison.Ordinal))
            {
                value = ReadWithFormatter(ref reader, options, formatter);
                found = true;
            }
            else
            {
                reader.Skip();
            }
        }

        if (!found)
        {
            throw new MessagePackSerializationException(
                "The Configlue MessagePack envelope has no '$value' entry."
            );
        }

        return value;
    }

    internal static object? ReadValue(
        Type type,
        in ReadOnlySequence<byte> source,
        MessagePackSerializerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        var reader = new MessagePackReader(source);
        if (reader.TryReadNil())
        {
            return null;
        }

        if (!TryReadEnvelopeHeader(reader.CreatePeekReader()))
        {
            return MessagePackSerializer.Deserialize(type, ref reader, options);
        }

        var count = reader.ReadMapHeader();
        object? value = null;
        var found = false;
        for (var index = 0; index < count; index++)
        {
            var key = reader.ReadString();
            if (string.Equals(key, PayloadProperty, StringComparison.Ordinal))
            {
                value = MessagePackSerializer.Deserialize(type, ref reader, options);
                found = true;
            }
            else
            {
                reader.Skip();
            }
        }

        if (!found)
        {
            throw new MessagePackSerializationException(
                "The Configlue MessagePack envelope has no '$value' entry."
            );
        }

        return value;
    }

    private static T? ReadWithFormatter<T>(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options,
        IMessagePackFormatter<T> formatter
    )
    {
        try
        {
            return formatter.Deserialize(ref reader, options);
        }
        catch (Exception exception)
            when (exception is EndOfStreamException or InsufficientExecutionStackException)
        {
            throw new MessagePackSerializationException(
                "The MessagePack payload is truncated or exceeds the configured depth limit.",
                exception
            );
        }
    }

    internal static StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source)
    {
        var reader = new MessagePackReader(source);
        if (reader.TryReadNil() || !TryReadEnvelopeHeader(reader.CreatePeekReader()))
        {
            return null;
        }

        var count = reader.ReadMapHeader();
        StateSchemaMetadata? schema = null;
        for (var index = 0; index < count; index++)
        {
            var key = reader.ReadString();
            if (string.Equals(key, MetadataProperty, StringComparison.Ordinal))
            {
                schema = ReadSchema(ref reader);
            }
            else
            {
                reader.Skip();
            }
        }

        return schema;
    }

    internal static T? ReadPayload<T>(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options,
        IMessagePackFormatter<T>? formatter
    )
    {
        try
        {
            return formatter is not null
                ? formatter.Deserialize(ref reader, options)
                : MessagePackSerializer.Deserialize<T>(ref reader, options);
        }
        catch (Exception exception)
            when (exception is EndOfStreamException or InsufficientExecutionStackException)
        {
            throw new MessagePackSerializationException(
                "The MessagePack payload is truncated or exceeds the configured depth limit.",
                exception
            );
        }
    }

    internal static bool IsRecoverableReadException(Exception exception)
    {
        if (exception is not MessagePackSerializationException)
        {
            return false;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is FormatterNotRegisteredException)
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteSchema(ref MessagePackWriter writer, StateSchemaMetadata? schema)
    {
        if (schema is not { } metadata)
        {
            writer.WriteMapHeader(0);
            return;
        }

        if (metadata.ModelId is { } modelId)
        {
            writer.WriteMapHeader(2);
            writer.Write(VersionProperty);
            writer.Write(metadata.Version);
            writer.Write(IdProperty);
            writer.Write(modelId);
            return;
        }

        writer.WriteMapHeader(1);
        writer.Write(VersionProperty);
        writer.Write(metadata.Version);
    }

    private static StateSchemaMetadata? ReadSchema(ref MessagePackReader reader)
    {
        if (!reader.TryReadMapHeader(out var count))
        {
            throw new MessagePackSerializationException(
                "The '$configlue' metadata must be a MessagePack map."
            );
        }

        if (count == 0)
        {
            return null;
        }

        string? modelId = null;
        int? version = null;
        for (var index = 0; index < count; index++)
        {
            var key = reader.ReadString();
            if (string.Equals(key, VersionProperty, StringComparison.Ordinal))
            {
                version = reader.ReadInt32();
            }
            else if (string.Equals(key, IdProperty, StringComparison.Ordinal))
            {
                modelId = reader.ReadString();
            }
            else
            {
                reader.Skip();
            }
        }

        if (version is not int schemaVersion || schemaVersion < StateSchemaMetadata.InitialVersion)
        {
            throw new MessagePackSerializationException(
                "The '$configlue' metadata must contain a positive integer version."
            );
        }

        return new StateSchemaMetadata(modelId, schemaVersion);
    }

    private static bool TryReadEnvelopeHeader(MessagePackReader reader)
    {
        if (!reader.TryReadMapHeader(out var count))
        {
            return false;
        }

        for (var index = 0; index < count; index++)
        {
            if (reader.TryReadStringSpan(out var key))
            {
                if (
                    key.SequenceEqual(MetadataPropertyUtf8)
                    || key.SequenceEqual(PayloadPropertyUtf8)
                )
                {
                    return true;
                }
            }
            else
            {
                reader.Skip();
            }

            reader.Skip();
        }

        return false;
    }
}
