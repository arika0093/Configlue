using System.Security.Cryptography;

namespace Configlue.Transformer.AES;

/// <summary>Encrypts state bytes with AES-GCM and authenticates them when read.</summary>
/// <remarks>
/// Stored content is a version byte followed by a 12-byte nonce, a 16-byte tag, and ciphertext.
/// The transformer does not own or derive its key; dispose it when the key is no longer needed.
/// </remarks>
public sealed class AesGcmStateByteTransformer
    : IStateByteTransformer,
        IStateByteTransformerRecoveryPolicy,
        IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;
    private const byte FormatVersion = 1;

    private readonly byte[] _key;
    private bool _disposed;

    /// <summary>Creates a transformer using an AES key of 128, 192, or 256 bits.</summary>
    public AesGcmStateByteTransformer(ReadOnlySpan<byte> key)
    {
        if (key.Length is not (16 or 24 or 32))
        {
            throw new ArgumentException(
                "An AES key must contain 16, 24, or 32 bytes.",
                nameof(key)
            );
        }

        _key = key.ToArray();
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source)
    {
        ThrowIfDisposed();
        if (source.Length < HeaderSize)
        {
            throw new CryptographicException("The encrypted state content is truncated.");
        }

        var encoded = source.Span;
        if (encoded[0] != FormatVersion)
        {
            throw new CryptographicException(
                "The encrypted state content has an unsupported version."
            );
        }

        var plaintext = new byte[source.Length - HeaderSize];
#if NETSTANDARD2_1
        using var aes = new AesGcm(_key);
#else
        using var aes = new AesGcm(_key, TagSize);
#endif
        aes.Decrypt(
            encoded.Slice(1, NonceSize),
            encoded[HeaderSize..],
            encoded.Slice(1 + NonceSize, TagSize),
            plaintext
        );
        return plaintext;
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source)
    {
        ThrowIfDisposed();
        var encrypted = new byte[HeaderSize + source.Length];
        encrypted[0] = FormatVersion;
        var nonce = encrypted.AsSpan(1, NonceSize);
        var tag = encrypted.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = encrypted.AsSpan(HeaderSize);
        RandomNumberGenerator.Fill(nonce);
#if NETSTANDARD2_1
        using var aes = new AesGcm(_key);
#else
        using var aes = new AesGcm(_key, TagSize);
#endif
        aes.Encrypt(nonce, source.Span, ciphertext, tag);
        return encrypted;
    }

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) =>
        exception is CryptographicException;

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AesGcmStateByteTransformer));
        }
    }
}
