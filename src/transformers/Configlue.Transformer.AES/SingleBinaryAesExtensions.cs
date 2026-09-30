using Configlue.Source.Presets;

namespace Configlue.Transformer.AES;

/// <summary>AES encryption conveniences for the SingleBinary preset.</summary>
public static class SingleBinaryAesExtensions
{
    /// <summary>Encrypts the complete ZIP archive using a 128, 192, or 256-bit AES key.</summary>
    /// <remarks>The key memory is retained until all sources are materialized.</remarks>
    public static SingleBinaryBuilder WithAesKey(
        this SingleBinaryBuilder builder,
        ReadOnlyMemory<byte> key
    )
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (key.Length is not (16 or 24 or 32))
        {
            throw new ArgumentException(
                "An AES key must contain 16, 24, or 32 bytes.",
                nameof(key)
            );
        }

        return builder.WithEncryption(() => new AesGcmStateByteTransformer(key.Span));
    }

    /// <summary>Encrypts the complete ZIP archive using an AES-GCM key derived from a passphrase.</summary>
    public static SingleBinaryBuilder WithPassphrase(
        this SingleBinaryBuilder builder,
        string passphrase
    )
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (passphrase is null)
        {
            throw new ArgumentNullException(nameof(passphrase));
        }
        if (passphrase.Length == 0)
        {
            throw new ArgumentException("A passphrase cannot be empty.", nameof(passphrase));
        }

        return builder.WithEncryption(() => new AesGcmPassphraseStateByteTransformer(passphrase));
    }

    /// <summary>Encrypts the complete archive using a passphrase.</summary>
    /// <remarks>This is a convenience alias for <see cref="WithPassphrase"/>.</remarks>
    public static SingleBinaryBuilder WithEncrypted(
        this SingleBinaryBuilder builder,
        string passphrase
    ) => WithPassphrase(builder, passphrase);
}
