using System.Buffers;

namespace Configlue;

/// <summary>Writes a typed state value by composing a codec and a resource.</summary>
public sealed class SerializedStateWriter<T> : IStateWriter<T>
{
    private readonly IResourceWriter _resource;
    private readonly object _codec;
    private readonly StateCodecContext _context;

    /// <summary>Creates a serialized state writer.</summary>
    public SerializedStateWriter(IResourceWriter resource, object codec, StateCodecContext context = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(codec);
        if (codec is not IStateCodec<T> && codec is not IStateCodec)
        {
            throw new ArgumentException("The codec must implement IStateCodec or IStateCodec<T>.", nameof(codec));
        }

        _resource = resource;
        _codec = codec;
        _context = context;
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                throw new InvalidOperationException("The codec does not implement a supported state codec interface.");
        }

        var schema = context.Schema;
        return _resource.WriteAsync(
            new ResourceWriteRequest(destination.WrittenMemory, request.ExpectedRevision, schema),
            cancellationToken);
    }
}
