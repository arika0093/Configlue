using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using Configlue.Transformers;

namespace Configlue.Extensibility;

/// <summary>
/// A serialized source that composes one resource with a codec and optional byte transformers,
/// exposing its read, write, watch, batch, and identity capabilities as a single object.
/// </summary>
public sealed class SerializedSource<T>
    : ISourceWatcher,
        ISourceWriteBatchParticipant<T>,
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
        object codec,
        StateCodecContext context = default,
        StateSchemaDispatcher<T>? schemaDispatcher = null,
        IEnumerable<IStateByteTransformer>? transformers = null,
        IResourceWriter? writer = null,
        ISourceWatcher? watcher = null,
        IEnumerable<IStateMiddleware<T>>? middlewares = null
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
            reader =
                middlewarePipeline[index].WrapReader(reader)
                ?? throw new InvalidOperationException(
                    "A middleware returned a null state reader."
                );
            if (stateWriter is not null)
            {
                stateWriter =
                    middlewarePipeline[index].WrapWriter(stateWriter)
                    ?? throw new InvalidOperationException(
                        "A middleware returned a null state writer."
                    );
            }
        }

        _reader = reader;
        _writer = stateWriter;
    }

    /// <inheritdoc />
    public bool CanWrite => _writer is not null;

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
    ) =>
        _writer is null
            ? throw new InvalidOperationException("This serialized source does not support writes.")
            : _writer.WriteAsync(context, request, cancellationToken);

    /// <inheritdoc />
    public bool TryCreateBatchWrite(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        out ResourceId resourceId,
        out IResourceBatchWriter? batchWriter,
        out ResourceWriteMutation? mutation
    )
    {
        if (_writer is ISourceWriteBatchParticipant<T> participant)
        {
            return participant.TryCreateBatchWrite(
                context,
                request,
                out resourceId,
                out batchWriter,
                out mutation
            );
        }

        resourceId = default;
        batchWriter = null;
        mutation = null;
        return false;
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
