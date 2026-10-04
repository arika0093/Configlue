using System.Buffers;
using Configlue.Codecs;
using Configlue.Sources;
using Configlue.State;

namespace Configlue.Extensibility;

/// <summary>Writes a typed state value by composing a codec and a resource.</summary>
/// <remarks>
/// This writer is an internal implementation detail of <see cref="SerializedSource{T}"/>, which is
/// the canonical provider-author composition object. Provider authors must not construct this
/// writer directly; <see cref="SerializedSource{T}"/> builds the reader and writer together from
/// one backing resource so a single Resource never needs manual separate construction.
/// </remarks>
internal sealed class SerializedStateWriter<T>
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
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var resourceRequest = await CreateResourceRequestAsync(request, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await _resource
                .WriteAsync(context, resourceRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            resourceRequest.ContentOwner?.Dispose();
        }
    }

    /// <summary>Prepares a resource batch mutation for one logical subject.</summary>
    public async ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var resourceRequest = await CreateResourceRequestAsync(request, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (
                _resource is IResourceBatchParticipant participant
                && participant.BatchWriter is { } participantWriter
            )
            {
                var plan = new StateWriteBatchPlan(
                    ResourceContextExtensions.GetResourceId(participant, context),
                    participantWriter,
                    participant.CreateMutation(context, resourceRequest)
                );
                plan.SetContentOwner(resourceRequest.ContentOwner);
                return plan;
            }

            if (_resource is IResourceBatchWriter writer)
            {
                var plan = new StateWriteBatchPlan(
                    ResourceContextExtensions.GetResourceId((IResourceIdentity)writer, context),
                    writer,
                    ResourceWriteMutation.Replace(resourceRequest, context)
                );
                plan.SetContentOwner(resourceRequest.ContentOwner);
                return plan;
            }

            resourceRequest.ContentOwner?.Dispose();
            return null;
        }
        catch
        {
            resourceRequest.ContentOwner?.Dispose();
            throw;
        }
    }

    private async ValueTask<ResourceWriteRequest> CreateResourceRequestAsync(
        StateWriteRequest<T> request,
        CancellationToken cancellationToken
    )
    {
        var destination = new PooledBufferWriter();
        var context = _context;
        try
        {
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
                ?? (
                    request.Value is IConfiglueFragment fragment
                        ? fragment.Schema.ToMetadata()
                        : null
                );
            var content = await StateByteTransformerPipeline
                .TransformWriteAsync(destination.WrittenMemory, _transformers, cancellationToken)
                .ConfigureAwait(false);
            return new ResourceWriteRequest(content, Condition: request.Condition, Schema: schema)
            {
                ContentIsOwned = true,
                ContentOwner = destination,
            };
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }
}
