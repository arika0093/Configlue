using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using MessagePack;
using MessagePack.Formatters;

namespace Configlue.Provider.MessagePack;

/// <summary>Default options used by MessagePack codecs when the caller supplies none.</summary>
/// <remarks>
/// Constructed on demand inside the annotated accessor so merely referencing this type
/// (for example to use the explicit-options constructors from NativeAOT code) never roots
/// the standard resolver's dynamic fallback.
/// </remarks>
internal static class MessagePackStateCodecDefaults
{
    [RequiresDynamicCode(
        "The default resolver may generate formatters through runtime reflection."
    )]
    public static MessagePackSerializerOptions GetOptions() =>
        MessagePackSerializerOptions.Standard.WithResolver(new ConfiglueMessagePackResolver());
}

/// <summary>A MessagePack state codec for values discovered through a configured resolver.</summary>
/// <remarks>
/// This codec uses <see cref="MessagePackSerializer"/>'s runtime type resolution and is intended for
/// ordinary model types. Generated fragments should use <see cref="MessagePackStateCodec{T}"/> so
/// their formatters are resolved without reflection.
/// </remarks>
public sealed class MessagePackStateCodec
    : IStateCodec,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    private readonly MessagePackSerializerOptions _options;

    /// <summary>Creates a codec with the supplied MessagePack options.</summary>
    /// <remarks>
    /// A <see langword="null"/> options value selects the default resolver, which can generate
    /// formatters through runtime reflection and keeps MessagePack's dynamic fallback reachable
    /// for trimming and NativeAOT. NativeAOT hosts must pass explicit options built over a
    /// closed-world resolver.
    /// </remarks>
    [RequiresDynamicCode(
        "The default resolver may generate formatters through runtime reflection."
    )]
    public MessagePackStateCodec(MessagePackSerializerOptions? options = null)
    {
        _options = options ?? MessagePackStateCodecDefaults.GetOptions();
    }

    /// <inheritdoc />
    [RequiresDynamicCode(
        "Resolving a formatter from a runtime type may generate code through runtime reflection."
    )]
    public object? Deserialize(
        Type type,
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    ) => MessagePackStateCodecOperations.ReadDynamicValue(type, in source, _options);

    /// <inheritdoc />
    [RequiresDynamicCode(
        "Resolving a formatter from a runtime type may generate code through runtime reflection."
    )]
    public void Serialize(
        Type type,
        object? value,
        IBufferWriter<byte> destination,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(destination);
        var writer = new MessagePackWriter(destination);
        var schema =
            context.Schema
            ?? (value is IConfiglueFragment fragment ? fragment.Schema.ToMetadata() : null);
        MessagePackStateCodecOperations.WriteEnvelope(ref writer, type, value, schema, _options);
        writer.Flush();
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        MessagePackStateCodecOperations.ReadSchemaMetadata(in source);

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) =>
        MessagePackStateCodecOperations.IsRecoverableReadException(exception);
}

/// <summary>A typed MessagePack state codec with generated-fragment support.</summary>
/// <typeparam name="T">The state or generated fragment type.</typeparam>
public sealed class MessagePackStateCodec<T>
    : IStateCodec<T>,
        IStateCodecWithMetadata<T>,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    private readonly MessagePackSerializerOptions _options;
    private readonly IMessagePackFormatter<T>? _formatter;

    /// <summary>Creates a codec with the default MessagePack options.</summary>
    [RequiresDynamicCode(
        "The default resolver may generate formatters through runtime reflection."
    )]
    public MessagePackStateCodec()
    {
        _options = MessagePackStateCodecDefaults.GetOptions();
        _formatter = ConfiglueMessagePackFragmentRegistry.GetOrNull<T>();
    }

    /// <summary>Creates a codec with the supplied MessagePack options.</summary>
    public MessagePackStateCodec(MessagePackSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _formatter = ConfiglueMessagePackFragmentRegistry.GetOrNull<T>();
    }

    /// <summary>Gets the generated fragment formatter, if this type is a registered fragment.</summary>
    internal IMessagePackFormatter<T>? Formatter => _formatter;

    /// <inheritdoc />
    public T? Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context) =>
        MessagePackStateCodecOperations.ReadValue(in source, _options, _formatter);

    /// <inheritdoc />
    public StateCodecDecodeResult<T> DeserializeWithMetadata(
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    ) => MessagePackStateCodecOperations.DecodeWithMetadata(in source, _options, _formatter);

    /// <inheritdoc />
    public void Serialize(T? value, IBufferWriter<byte> destination, in StateCodecContext context)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var writer = new MessagePackWriter(destination);
        var schema =
            context.Schema
            ?? (value is IConfiglueFragment fragment ? fragment.Schema.ToMetadata() : null);
        MessagePackStateCodecOperations.WriteEnvelope(
            ref writer,
            value,
            schema,
            _options,
            _formatter
        );
        writer.Flush();
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        MessagePackStateCodecOperations.ReadSchemaMetadata(in source);

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) =>
        MessagePackStateCodecOperations.IsRecoverableReadException(exception);
}
