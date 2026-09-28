using System.Buffers;

namespace Configlue.State;

/// <summary>Writes a typed state value by composing a codec and a resource.</summary>
public sealed class SerializedStateWriter<T> : IStateWriter<T>, IStateWriteBatchParticipant<T>
{
    private readonly IResourceWriter _resource;
    private readonly object _codec;
    private readonly StateCodecContext _context;
    private readonly IStateByteTransformer[] _transformers;

    /// <summary>Creates a serialized state writer.</summary>
    public SerializedStateWriter(
        IResourceWriter resource,
        object codec,
        StateCodecContext context = default,
        IEnumerable<IStateByteTransformer>? transformers = null
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(codec);
        if (codec is not IStateCodec<T> && codec is not IStateCodec)
        {
            throw new ArgumentException(
                "The codec must implement IStateCodec or IStateCodec<T>.",
                nameof(codec)
            );
        }

        _resource = resource;
        _codec = codec;
        _context = context;
        _transformers = StateByteTransformerPipeline.Create(transformers);
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _resource.WriteAsync(CreateResourceRequest(request), cancellationToken);
    }

    /// <inheritdoc />
    public bool TryCreateBatchWrite(
        StateWriteRequest<T> request,
        out ResourceId resourceId,
        out IResourceBatchWriter? batchWriter,
        out ResourceWriteMutation? mutation
    )
    {
        var resourceRequest = CreateResourceRequest(request);
        if (
            _resource is IResourceBatchParticipant participant
            && participant.BatchWriter is { } participantWriter
        )
        {
            resourceId = participant.ResourceId;
            batchWriter = participantWriter;
            mutation = participant.CreateMutation(resourceRequest);
            return true;
        }

        if (_resource is IResourceBatchWriter writer)
        {
            resourceId = writer.ResourceId;
            batchWriter = writer;
            mutation = ResourceWriteMutation.Replace(resourceRequest);
            return true;
        }

        resourceId = default;
        batchWriter = null;
        mutation = null;
        return false;
    }

    private ResourceWriteRequest CreateResourceRequest(StateWriteRequest<T> request)
    {
        var destination = new ArrayBufferWriter<byte>();
        var context = _context;
        switch (_codec)
        {
            case IStateCodec<T> typed:
                typed.Serialize(request.Value, destination, in context);
                break;
            case IStateCodec untyped:
                untyped.Serialize(typeof(T), request.Value, destination, in context);
                break;
            default:
                throw new InvalidOperationException(
                    "The codec does not implement a supported state codec interface."
                );
        }

        var schema =
            context.Schema
            ?? (request.Value is IConfiglueFragment fragment ? fragment.Schema.ToMetadata() : null);
        return new ResourceWriteRequest(
            StateByteTransformerPipeline.TransformWrite(destination.WrittenMemory, _transformers),
            request.ExpectedRevision,
            schema,
            request.CheckRevision
        )
        {
            ContentIsOwned = true,
        };
    }
}
