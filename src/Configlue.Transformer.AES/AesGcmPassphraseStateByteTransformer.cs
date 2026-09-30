using System.Security.Cryptography;

namespace Configlue.Transformer.AES;

/// <summary>Encrypts state bytes with AES-GCM using a passphrase-derived key.</summary>
/// <remarks>
/// The encoded content includes a format version, random salt, and nonce. The passphrase is not
/// stored in the encoded content; dispose the transformer when it is no longer needed.
/// </remarks>
public sealed class AesGcmPassphraseStateByteTransformer
    : IStateByteTransformer,
        IStateByteTransformerRecoveryPolicy,
        IDisposable
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 5 + SaltSize + NonceSize + TagSize;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 600_000;
    private static ReadOnlySpan<byte> FormatMarker => "CLAE1"u8;

    private string? _passphrase;

    /// <summary>Creates a transformer using PBKDF2-HMAC-SHA-256 and AES-256-GCM.</summary>
    public AesGcmPassphraseStateByteTransformer(string passphrase)
    {
        if (passphrase is null)
        {
            throw new ArgumentNullException(nameof(passphrase));
        }
        if (passphrase.Length == 0)
        {
            throw new ArgumentException("A passphrase cannot be empty.", nameof(passphrase));
        }

        _passphrase = passphrase;
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source)
    {
        var passphrase = GetPassphrase();
        if (source.Length < HeaderSize)
        {
            throw new CryptographicException("The encrypted state content is truncated.");
        }

        var encoded = source.Span;
        if (!encoded[..FormatMarker.Length].SequenceEqual(FormatMarker))
        {
            throw new CryptographicException(
                "The encrypted state content has an unsupported format."
            );
        }

        Span<byte> key = stackalloc byte[KeySize];
        DeriveKey(passphrase, encoded.Slice(FormatMarker.Length, SaltSize), key);
        try
        {
            var plaintext = new byte[source.Length - HeaderSize];
#if NETSTANDARD2_1
            using var aes = new AesGcm(key.ToArray());
#else
            using var aes = new AesGcm(key, TagSize);
#endif
            aes.Decrypt(
                encoded.Slice(FormatMarker.Length + SaltSize, NonceSize),
                encoded[HeaderSize..],
                encoded.Slice(FormatMarker.Length + SaltSize + NonceSize, TagSize),
                plaintext
            );
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source)
    {
        var passphrase = GetPassphrase();
        var encrypted = new byte[HeaderSize + source.Length];
        var salt = encrypted.AsSpan(FormatMarker.Length, SaltSize);
        var nonce = encrypted.AsSpan(FormatMarker.Length + SaltSize, NonceSize);
        var tag = encrypted.AsSpan(FormatMarker.Length + SaltSize + NonceSize, TagSize);
        FormatMarker.CopyTo(encrypted);
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        Span<byte> key = stackalloc byte[KeySize];
        DeriveKey(passphrase, salt, key);
        try
        {
#if NETSTANDARD2_1
            using var aes = new AesGcm(key.ToArray());
#else
            using var aes = new AesGcm(key, TagSize);
#endif
            aes.Encrypt(nonce, source.Span, encrypted.AsSpan(HeaderSize), tag);
            return encrypted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) =>
        exception is CryptographicException;

    /// <inheritdoc />
    public void Dispose()
    {
        _passphrase = null;
    }

    private string GetPassphrase() =>
        _passphrase
        ?? throw new ObjectDisposedException(nameof(AesGcmPassphraseStateByteTransformer));

    private static void DeriveKey(string passphrase, ReadOnlySpan<byte> salt, Span<byte> key)
    {
#if NETSTANDARD2_1
        using var derivation = new Rfc2898DeriveBytes(
            passphrase,
            salt.ToArray(),
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256
        );
        var derivedKey = derivation.GetBytes(key.Length);
        derivedKey.CopyTo(key);
        CryptographicOperations.ZeroMemory(derivedKey);
#else
        Rfc2898DeriveBytes.Pbkdf2(
            passphrase.AsSpan(),
            salt,
            key,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256
        );
#endif
    }
}
