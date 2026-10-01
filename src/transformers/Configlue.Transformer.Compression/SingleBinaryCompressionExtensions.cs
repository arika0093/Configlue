using Configlue.Source.Presets;

namespace Configlue.Transformer.Compression;

/// <summary>Compression conveniences for the SingleBinary preset.</summary>
public static class SingleBinaryCompressionExtensions
{
    /// <summary>Compresses the complete ZIP archive with LZ4.</summary>
    public static SingleBinaryBuilder WithLz4(
        this SingleBinaryBuilder builder,
        int? compressionLevel = null
    ) => WithCompression(builder, CompressionAlgorithm.Lz4, compressionLevel);

    /// <summary>Compresses the complete ZIP archive with Zstandard.</summary>
    public static SingleBinaryBuilder WithZstandard(
        this SingleBinaryBuilder builder,
        int? compressionLevel = null
    ) => WithCompression(builder, CompressionAlgorithm.Zstandard, compressionLevel);

    /// <summary>Compresses the complete ZIP archive with the selected algorithm.</summary>
    public static SingleBinaryBuilder WithCompression(
        this SingleBinaryBuilder builder,
        CompressionAlgorithm algorithm,
        int? compressionLevel = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        var transformer = compressionLevel is { } level
            ? new CompressionStateByteTransformer(algorithm, level)
            : new CompressionStateByteTransformer(algorithm);
        return builder.WithEncryption(transformer);
    }
}
