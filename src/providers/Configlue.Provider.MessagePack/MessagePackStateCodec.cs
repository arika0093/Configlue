using System.Buffers;
using MessagePack;
using MessagePack.Formatters;

namespace Configlue.Provider.MessagePack;

/// <summary>Default options used by MessagePack codecs when the caller supplies none.</summary>
internal static class MessagePackStateCodecDefaults
{
    public static MessagePackSerializerOptions Options { get; } =
        MessagePackSerializerOptions.Standard.WithResolver(ConfiglueMessagePackResolver.Instance);
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
    public MessagePackStateCodec(MessagePackSerializerOptions? options = null)
    {
        _options = options ?? MessagePackStateCodecDefaults.Options;
    }

    /// <inheritdoc />
    public object? Deserialize(
        Type type,
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    ) => MessagePackStateCodecOperations.ReadValue(type, in source, _options);

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

/// <summary>A typed MessagePack state codec for generated Configlue fragments.</summary>
/// <remarks>
/// The generated fragment formatter must be registered before the codec is created. This codec invokes that
/// formatter directly; it does not fall back to runtime type resolution. Use the non-generic
/// <see cref="MessagePackStateCodec"/> for ordinary runtime-resolved model types.
/// </remarks>
/// <typeparam name="T">The generated fragment type.</typeparam>
public sealed class MessagePackStateCodec<T>
    : IStateCodec<T>,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    private readonly MessagePackSerializerOptions _options;
    private readonly IMessagePackFormatter<T> _formatter;

    /// <summary>Creates a codec with the supplied MessagePack options.</summary>
    public MessagePackStateCodec(MessagePackSerializerOptions? options = null)
    {
        _options = options ?? MessagePackStateCodecDefaults.Options;
        _formatter =
            ConfiglueMessagePackFragmentRegistry.GetOrNull<T>()
            ?? throw new InvalidOperationException(
                $"The typed MessagePack state codec requires a registered generated Configlue fragment formatter for '{typeof(T)}'. Use the non-generic MessagePackStateCodec for runtime-resolved types."
            );
    }

    private MessagePackStateCodec(
        MessagePackSerializerOptions options,
        IMessagePackFormatter<T> formatter
    )
    {
        _options = options;
        _formatter = formatter;
    }

    internal static MessagePackStateCodec<T> CreateGeneratedFragment(
        MessagePackSerializerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!ConfiglueMessagePackFragmentRegistry.TryGetFormatter<T>(out var formatter))
        {
            throw new InvalidOperationException(
                $"A generated Configlue fragment formatter for '{typeof(T)}' must be registered before creating its typed MessagePack codec."
            );
        }

        return new MessagePackStateCodec<T>(options, formatter);
    }

    /// <summary>Gets the registered generated fragment formatter.</summary>
    internal IMessagePackFormatter<T> Formatter => _formatter;

    /// <inheritdoc />
    public T? Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context) =>
        MessagePackStateCodecOperations.ReadValue(in source, _options, _formatter);

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
