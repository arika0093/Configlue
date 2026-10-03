using NativeCompressions;

namespace Configlue.Transformer.Compression;

/// <summary>Compresses and decompresses state bytes with a general-purpose algorithm.</summary>
/// <remarks>
/// The transformer is stateless and thread-safe; a single instance can be shared by all resources.
/// Stored content is a native frame with a self-describing header, so no Configlue-specific
/// versioning is added. Register an AES transformer before a compression transformer in a resource
/// transformer list so bytes are compressed before they are encrypted and decrypted before they are
/// decompressed.
/// </remarks>
public sealed class CompressionStateByteTransformer
    : ISynchronousStateByteTransformer,
        IStateByteTransformerRecoveryPolicy
{
    private const int DefaultZstandardLevel = 3;
    private const int Lz4MinLevel = 0;
    private const int Lz4MaxLevel = 12;
    private const int ZstandardMinLevel = 1;
    private const int ZstandardMaxLevel = 22;

    private readonly CompressionAlgorithm _algorithm;
    private readonly int? _compressionLevel;

    /// <summary>Creates a transformer for the algorithm using its default compression level.</summary>
    public CompressionStateByteTransformer(CompressionAlgorithm algorithm)
        : this(algorithm, compressionLevel: null) { }

    /// <summary>Creates a transformer for the algorithm using an explicit compression level.</summary>
    /// <param name="algorithm">The compression algorithm.</param>
    /// <param name="compressionLevel">The algorithm-specific compression level.</param>
    public CompressionStateByteTransformer(CompressionAlgorithm algorithm, int compressionLevel)
        : this(algorithm, (int?)compressionLevel) { }

    private CompressionStateByteTransformer(CompressionAlgorithm algorithm, int? compressionLevel)
    {
        if (!Enum.IsDefined(typeof(CompressionAlgorithm), algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm));
        }

        if (compressionLevel is { } level)
        {
            var (minimum, maximum) = algorithm switch
            {
                CompressionAlgorithm.Lz4 => (Lz4MinLevel, Lz4MaxLevel),
                _ => (ZstandardMinLevel, ZstandardMaxLevel),
            };
            if (level < minimum || level > maximum)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(compressionLevel),
                    level,
                    $"The {algorithm} compression level must be between {minimum} and {maximum}."
                );
            }
        }

        _algorithm = algorithm;
        _compressionLevel = compressionLevel;
    }

    /// <summary>The compression algorithm used by this transformer.</summary>
    public CompressionAlgorithm Algorithm => _algorithm;

    /// <summary>Creates an LZ4 transformer, optionally at an explicit compression level.</summary>
    public static CompressionStateByteTransformer Lz4(int? compressionLevel = null) =>
        compressionLevel is { } level
            ? new CompressionStateByteTransformer(CompressionAlgorithm.Lz4, level)
            : new CompressionStateByteTransformer(CompressionAlgorithm.Lz4);

    /// <summary>Creates a Zstandard transformer, optionally at an explicit compression level.</summary>
    public static CompressionStateByteTransformer Zstandard(int? compressionLevel = null) =>
        compressionLevel is { } level
            ? new CompressionStateByteTransformer(CompressionAlgorithm.Zstandard, level)
            : new CompressionStateByteTransformer(CompressionAlgorithm.Zstandard);

    /// <inheritdoc />
    public ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source)
    {
        try
        {
            return _algorithm switch
            {
                CompressionAlgorithm.Lz4 => LZ4.Decompress(source.Span, trustedData: false),
                CompressionAlgorithm.Zstandard => NativeCompressions.Zstandard.Decompress(
                    source.Span,
                    trustedData: false
                ),
                _ => throw new InvalidOperationException(
                    "The transformer has an unknown algorithm."
                ),
            };
        }
        catch (Exception exception)
            when (exception is LZ4Exception or ZstandardException or InvalidOperationException)
        {
            throw new CompressionException("The compressed state content is malformed.", exception);
        }
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source) =>
        _algorithm switch
        {
            CompressionAlgorithm.Lz4 => CompressLz4(source.Span),
            CompressionAlgorithm.Zstandard => NativeCompressions.Zstandard.Compress(
                source.Span,
                _compressionLevel ?? DefaultZstandardLevel
            ),
            _ => throw new InvalidOperationException("The transformer has an unknown algorithm."),
        };

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) =>
        exception is CompressionException;

    private byte[] CompressLz4(ReadOnlySpan<byte> source)
    {
        if (_compressionLevel is not { } level)
        {
            return LZ4.Compress(source);
        }

        var options = LZ4CompressionOptions.Default with { CompressionLevel = level };
        return LZ4.Compress(source, options);
    }
}
