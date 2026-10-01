using System.Buffers;
using Configlue.Sources;

namespace Configlue.Extensibility;

/// <summary>Writes a typed state value by composing a codec and a resource.</summary>
public sealed class SerializedStateWriter<T>
    : ISourceWriter<T>,
        ISourceWriteBatchParticipant<T>,
        ITryContextualResourceIdentity
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
    public ResourceId ResourceId => GetResourceId(ConfiglueResourceContext.Default);

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        TryGetResourceId(context, out var resourceId)
            ? resourceId
            : throw new InvalidOperationException(
                "The underlying resource has no physical identity."
            );

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        if (_resource is IResourceIdentity identity)
        {
            return identity.TryGetResourceId(context, out resourceId);
        }

        resourceId = default;
        return false;
    }

    /// <summary>Writes serialized state for one logical subject and source-specific key.</summary>
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _resource.WriteAsync(context, CreateResourceRequest(request), cancellationToken);
    }

    /// <summary>Prepares a resource batch mutation for one logical subject.</summary>
    public bool TryCreateBatchWrite(
        ConfiglueResourceContext context,
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
            resourceId = participant.GetResourceId(context);
            batchWriter = participantWriter;
            mutation = participant.CreateMutation(context, resourceRequest);
            return true;
        }

        if (_resource is IResourceBatchWriter writer)
        {
            resourceId = writer.GetResourceId(context);
            batchWriter = writer;
            mutation = ResourceWriteMutation.Replace(resourceRequest, context);
            return true;
        }

        resourceId = default;
        batchWriter = null;
        mutation = null;
        return false;
    }

    private ResourceWriteRequest CreateResourceRequest(StateWriteRequest<T> request)
    {
#if NETSTANDARD
        var destination = new ArrayBufferWriter<byte>();
#else
        var destination = new ArrayBufferWriter<byte>();
#endif
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
            Condition: request.Condition,
            Schema: schema
        )
        {
            ContentIsOwned = true,
        };
    }
}
