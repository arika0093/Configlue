using System.Buffers;
using System.IO.Hashing;
using System.Text;

namespace Configlue;

internal static class ConfiglueHashing
{
#if !NETSTANDARD
    private const int StackBufferLimit = 256;
#endif

    public static string GetXxHash3Hex(ReadOnlySpan<byte> content)
    {
        Span<byte> hash = stackalloc byte[sizeof(ulong)];
        XxHash3.Hash(content, hash);
        return Convert.ToHexString(hash);
    }

    public static string GetXxHash3Hex(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
#if NETSTANDARD
        return GetXxHash3Hex(Encoding.UTF8.GetBytes(content));
#else
        var byteCount = Encoding.UTF8.GetByteCount(content);
        if (byteCount <= StackBufferLimit)
        {
            Span<byte> encoded = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(content, encoded);
            return GetXxHash3Hex(encoded);
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var encoded = rented.AsSpan(0, byteCount);
            Encoding.UTF8.GetBytes(content, encoded);
            return GetXxHash3Hex(encoded);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
#endif
    }
}
