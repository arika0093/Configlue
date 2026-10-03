using System.Buffers;

namespace Configlue.Extensibility;

/// <summary>A growable buffer writer whose backing storage is returned to the shared pool.</summary>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[]? _buffer;
    private int _written;

    public PooledBufferWriter(int initialCapacity = 256)
    {
        if (initialCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        }
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 1));
    }

    public ReadOnlyMemory<byte> WrittenMemory => GetBuffer().AsMemory(0, _written);

    public void Advance(int count)
    {
        var buffer = GetBuffer();
        if (count < 0 || count > buffer.Length - _written)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return GetBuffer().AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return GetBuffer().AsSpan(_written);
    }

    public void Dispose()
    {
        var buffer = System.Threading.Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private void EnsureCapacity(int sizeHint)
    {
        var buffer = GetBuffer();
        if (sizeHint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        }
        sizeHint = Math.Max(sizeHint, 1);
        if (sizeHint <= buffer.Length - _written)
        {
            return;
        }

        var required = checked(_written + sizeHint);
        var replacement = ArrayPool<byte>.Shared.Rent(
            Math.Max(required, checked(buffer.Length * 2))
        );
        buffer.AsSpan(0, _written).CopyTo(replacement);
        _buffer = replacement;
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }

    private byte[] GetBuffer() =>
        _buffer ?? throw new ObjectDisposedException(nameof(PooledBufferWriter));
}
