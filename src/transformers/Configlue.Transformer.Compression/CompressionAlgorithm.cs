namespace Configlue.Transformer.Compression;

/// <summary>A general-purpose byte compression algorithm supported by Configlue.</summary>
public enum CompressionAlgorithm
{
    /// <summary>The LZ4 frame format, optimized for very fast decompression.</summary>
    Lz4 = 0,

    /// <summary>The Zstandard frame format, offering a configurable ratio and speed balance.</summary>
    Zstandard = 1,
}
