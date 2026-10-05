using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Xml;

namespace Configlue.Provider.Xml;

/// <summary>
/// An XML codec for ordinary models and generated sparse fragments. Sequence members use an
/// explicit materialization contract (issue #280): arrays, <c>List{T}</c> and
/// read-only-list interfaces, <c>HashSet{T}</c>/set interfaces, and dictionaries
/// (<c>Dictionary{TKey, TValue}</c>, <c>SortedDictionary{TKey, TValue}</c>,
/// <c>SortedList{TKey, TValue}</c> and their read-only interfaces) are materialized
/// with assignable values. Queues, stacks, concurrent collections,
/// <c>BlockingCollection{T}</c>, <c>PriorityQueue{TElement, TPriority}</c>,
/// <c>LinkedList{T}</c>, <c>SortedSet{T}</c>, observable/read-only wrappers and
/// immutable collections are unsupported here and fail with <see cref="XmlException"/>;
/// use a first-class shape or a custom policy instead.
/// On <c>netstandard2.0</c>, an <c>IReadOnlySet{T}</c> contract has no BCL identity
/// and no runtime proxy is emitted, so it is likewise unsupported.
/// </summary>
public sealed class XmlStateCodec
    : IStateCodec,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    /// <inheritdoc />
    [RequiresUnreferencedCode("XmlSerializer requires reflected model metadata.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    public object? Deserialize(
        Type type,
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        return XmlStateCodecOperations.Deserialize(type, in source);
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("XmlSerializer requires reflected model metadata.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    public void Serialize(
        Type type,
        object? value,
        IBufferWriter<byte> destination,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(destination);
        var bytes = XmlStateCodecOperations.Serialize(type, value, context.Schema);
        bytes.AsSpan().CopyTo(destination.GetSpan(bytes.Count));
        destination.Advance(bytes.Count);
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        XmlStateCodecOperations.ReadSchemaMetadata(in source);

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) =>
        exception is XmlException
        || exception is InvalidOperationException { InnerException: XmlException };
}

/// <summary>A typed XML codec fast path.</summary>
public sealed class XmlStateCodec<T>
    : IStateCodec<T>,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    /// <inheritdoc />
    [RequiresUnreferencedCode("XmlSerializer requires reflected model metadata.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    public T? Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context) =>
        (T?)XmlStateCodecOperations.Deserialize(typeof(T), in source);

    /// <inheritdoc />
    [RequiresUnreferencedCode("XmlSerializer requires reflected model metadata.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    public void Serialize(T? value, IBufferWriter<byte> destination, in StateCodecContext context)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var bytes = XmlStateCodecOperations.Serialize(typeof(T), value, context.Schema);
        bytes.AsSpan().CopyTo(destination.GetSpan(bytes.Count));
        destination.Advance(bytes.Count);
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        XmlStateCodecOperations.ReadSchemaMetadata(in source);

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) =>
        exception is XmlException
        || exception is InvalidOperationException { InnerException: XmlException };
}
