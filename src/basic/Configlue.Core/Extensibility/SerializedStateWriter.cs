using System.Buffers;
using Configlue.Codecs;
using Configlue.Sources;
using Configlue.State;

namespace Configlue.Extensibility;

/// <summary>Writes a typed state value by composing a codec and a resource.</summary>
public sealed class SerializedStateWriter<T>
    : ISourceWriter<T>,
        IAsyncSourceWriteBatchParticipant<T>,
        ITryResourceIdentity
{
    private readonly IResourceWriter _resource;
    private readonly IStateCodec<T>? _typedCodec;
    private readonly IStateCodec? _dynamicCodec;
    private readonly StateCodecContext _context;
    private readonly IStateByteTransformer[] _transformers;

    /// <summary>Creates a serialized state writer.</summary>
    public SerializedStateWriter(
        IResourceWriter resource,
        IStateCodec<T> codec,
        StateCodecContext context = default,
        IEnumerable<IStateByteTransformer>? transformers = null
    )
        : this(resource, StateCodecBinding.Typed(codec), context, transformers) { }

    /// <summary>Creates a serialized state writer from an explicit typed or dynamic codec binding.</summary>
    public SerializedStateWriter(
        IResourceWriter resource,
        StateCodecBinding codec,
        StateCodecContext context = default,
        IEnumerable<IStateByteTransformer>? transformers = null
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(codec);
        if (!codec.TryGetTyped<T>(out _typedCodec) && codec.DynamicCodec is null)
        {
            throw new ArgumentException(
                $"The codec binding is for '{codec.StateType}', not '{typeof(T)}'.",
                nameof(codec)
            );
        }

        _resource = resource;
        _dynamicCodec = codec.DynamicCodec;
        _context = context;
        _transformers = StateByteTransformerPipeline.Create(transformers);
    }

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        return _resource.TryGetResourceId(context, out resourceId);
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
    public ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resourceRequest = CreateResourceRequest(request);
        if (
            _resource is IResourceBatchParticipant participant
            && participant.BatchWriter is { } participantWriter
        )
        {
            return new ValueTask<StateWriteBatchPlan?>(
                new StateWriteBatchPlan(
                    ResourceContextExtensions.GetResourceId(participant, context),
                    participantWriter,
                    participant.CreateMutation(context, resourceRequest)
                )
            );
        }

        if (_resource is IResourceBatchWriter writer)
        {
            return new ValueTask<StateWriteBatchPlan?>(
                new StateWriteBatchPlan(
                    ResourceContextExtensions.GetResourceId((IResourceIdentity)writer, context),
                    writer,
                    ResourceWriteMutation.Replace(resourceRequest, context)
                )
            );
        }

        return new ValueTask<StateWriteBatchPlan?>((StateWriteBatchPlan?)null);
    }

    private ResourceWriteRequest CreateResourceRequest(StateWriteRequest<T> request)
    {
#if NETSTANDARD
        var destination = new ArrayBufferWriter<byte>();
#else
        var destination = new ArrayBufferWriter<byte>();
#endif
        var context = _context;
        if (_typedCodec is { } typedCodec)
        {
            typedCodec.Serialize(request.Value, destination, in context);
        }
        else
        {
            _dynamicCodec!.Serialize(typeof(T), request.Value, destination, in context);
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
