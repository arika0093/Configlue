using System.Buffers;

namespace Configlue;

/// <summary>Reads a typed state value by composing a resource and a codec.</summary>
public sealed class SerializedStateReader<T> : IStateReader<T>
{
    private readonly IResourceReader _resource;
    private readonly object _codec;
    private readonly StateCodecContext _context;

    /// <summary>Creates a serialized state reader.</summary>
    public SerializedStateReader(IResourceReader resource, object codec, StateCodecContext context = default)
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
    public async ValueTask<StateReadResult<T>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _resource.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (result.Status != StateReadStatus.Success)
        {
            return new StateReadResult<T>(result.Status, default, result.Revision, Schema: result.Schema);
        }

        var context = result.Schema is { } schema ? new StateCodecContext(schema, _context.Services) : _context;
        var bytes = new ReadOnlySequence<byte>(result.Content);
        var value = _codec switch
        {
            IStateCodec<T> typed => typed.Deserialize(in bytes, in context),
            IStateCodec untyped => (T?)untyped.Deserialize(typeof(T), in bytes, in context),
            _ => throw new InvalidOperationException("The codec does not implement a supported state codec interface."),
        };

        return StateReadResult<T>.Success(value, result.Revision, result.Schema);
    }
}
