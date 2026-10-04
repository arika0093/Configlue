using Configlue.Codecs;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using Configlue.Transformers;

namespace Configlue.Extensibility;

/// <summary>
/// A serialized source that composes one resource with a codec and optional byte transformers,
/// exposing its read, write, watch, batch, and identity capabilities as a single object.
/// </summary>
/// <remarks>
/// This is the canonical provider-author entry point for composing a serialized
/// <c>Resource -&gt; optional Transformers -&gt; Codec -&gt; typed Source capabilities</c> pipeline.
/// Provider authors create one <see cref="SerializedSource{T}"/> from a single backing resource
/// (passing the same resource as <c>writer</c> and <c>watcher</c> facets when it supports those
/// capabilities), wrap it in a <see cref="Sources.StateSource{T}"/> descriptor, and register it
/// through the source-registration SPI (<see cref="IConfiglueSourceDefinition"/> via
/// <see cref="IConfiglueSourceRegistrationSink.Add"/>).
/// Ordinary application code must not use this type directly; high-level provider registration
/// helpers (for example <c>sources.JsonFile(...)</c>) remain the recommended path.
/// <para>
/// Supported capabilities: typed <see cref="Codecs.IStateCodec{T}"/> or an explicitly dynamic
/// <see cref="Codecs.IStateCodec"/> selected via <see cref="Codecs.StateCodecBinding"/>, zero or
/// more <see cref="Transformers.IStateByteTransformer"/> values, an optional resource writer and
/// watcher, resource identity, batch writes, full <see cref="Resources.ConfiglueResourceContext"/>
/// propagation, schema metadata/migration via <c>StateSchemaDispatcher{T}</c>, and NativeAOT-friendly
/// typed codec paths. The reader and writer are always built together from the one resource, so a
/// provider never constructs separate serialized reader/writer objects for the same backing store.
/// </para>
/// </remarks>
/// <remarks>Provider SPI: the canonical Resource+Codec composition entry point. Hidden from ordinary
/// completion; application code uses provider registration helpers.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class SerializedSource<T>
    : ISourceWatcher,
        IAsyncSourceWriteBatchParticipant<T>,
        ISourceCapabilities<T>,
        ITryResourceIdentity
{
    private readonly IResourceReader _resource;
    private readonly ISourceWriter<T>? _writer;
    private readonly ISourceWatcher? _watcher;
    private readonly ISourceReader<T> _reader;

    /// <summary>
    /// Creates a serialized source. Write and watch capabilities are supplied explicitly as facets of
    /// the same source object.
    /// </summary>
    /// <param name="resource">The source of persisted bytes.</param>
    /// <param name="codec">The codec that converts between bytes and state values.</param>
    /// <param name="context">Additional codec context.</param>
    /// <param name="schemaDispatcher">An optional schema migration dispatcher.</param>
    /// <param name="transformers">Byte transformations in resource-to-codec read order.</param>
    /// <param name="writer">An optional resource writer.</param>
    /// <param name="watcher">An optional change watcher.</param>
    /// <param name="middlewares">Typed state decorators; the first middleware is outermost.</param>
    public SerializedSource(
        IResourceReader resource,
        IStateCodec<T> codec,
        StateCodecContext context = default,
        StateSchemaDispatcher<T>? schemaDispatcher = null,
        IEnumerable<IStateByteTransformer>? transformers = null,
        IResourceWriter? writer = null,
        ISourceWatcher? watcher = null,
        IEnumerable<object>? middlewares = null
    )
        : this(
            resource,
            StateCodecBinding.Typed(codec),
            context,
            schemaDispatcher,
            transformers,
            writer,
            watcher,
            middlewares
        ) { }

    /// <summary>
    /// Creates a serialized source from an explicit typed or dynamic codec binding. Write and watch
    /// capabilities are supplied explicitly as facets of the same source object.
    /// </summary>
    public SerializedSource(
        IResourceReader resource,
        StateCodecBinding codec,
        StateCodecContext context = default,
        StateSchemaDispatcher<T>? schemaDispatcher = null,
        IEnumerable<IStateByteTransformer>? transformers = null,
        IResourceWriter? writer = null,
        ISourceWatcher? watcher = null,
        IEnumerable<object>? middlewares = null
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(codec);
        _resource = resource;
        var resourceWriter = writer;
        _watcher = watcher;
        var middlewarePipeline = middlewares?.ToArray() ?? [];
        if (middlewarePipeline.Any(static middleware => middleware is null))
        {
            throw new ArgumentException("A middleware collection cannot contain null values.");
        }

        ISourceReader<T> reader = new SerializedStateReader<T>(
            resource,
            codec,
            context,
            schemaDispatcher,
            transformers
        );
        ISourceWriter<T>? stateWriter = resourceWriter is null
            ? null
            : new SerializedStateWriter<T>(resourceWriter, codec, context, transformers);
        for (var index = middlewarePipeline.Length - 1; index >= 0; index--)
        {
            if (middlewarePipeline[index] is IStateReaderMiddleware<T> readerMiddleware)
            {
                reader =
                    readerMiddleware.WrapReader(reader)
                    ?? throw new InvalidOperationException(
                        "A middleware returned a null state reader."
                    );
            }
            if (
                stateWriter is not null
                && middlewarePipeline[index] is IStateWriterMiddleware<T> writerMiddleware
            )
            {
                stateWriter =
                    writerMiddleware.WrapWriter(stateWriter)
                    ?? throw new InvalidOperationException(
                        "A middleware returned a null state writer."
                    );
            }
        }

        _reader = reader;
        _writer = stateWriter;
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public ISourceWriter<T>? Writer => _writer;

    /// <inheritdoc />
    public ISourceWatcher? Watcher => _watcher;

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        if (_writer.TryGetResourceId(context, out resourceId))
        {
            return true;
        }

        return _resource.TryGetResourceId(context, out resourceId);
    }

    /// <inheritdoc />
    public ValueTask<StateReadResult<T>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    ) => _reader.ReadAsync(context, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        return _writer is null
            ? throw new InvalidOperationException("This serialized source does not support writes.")
            : _writer.WriteAsync(context, request, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_writer is IAsyncSourceWriteBatchParticipant<T> participant)
        {
            return participant.TryCreateBatchWriteAsync(context, request, cancellationToken);
        }

        return new ValueTask<StateWriteBatchPlan?>((StateWriteBatchPlan?)null);
    }

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        if (_watcher is null)
        {
            return new ValueTask(
                Task.FromException(
                    new InvalidOperationException(
                        "This serialized source does not support watching."
                    )
                )
            );
        }

        return _watcher.WaitForChangeAsync(context, observedRevision, cancellationToken);
    }
}
