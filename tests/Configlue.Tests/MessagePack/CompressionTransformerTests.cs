using System.Text;
using Configlue.Transformer.Compression;

namespace Configlue.Tests;

public sealed class CompressionTransformerTests
{
    [Test]
    public void Lz4_RoundTripsAndRejectsMalformedInput()
    {
        var transformer = CompressionStateByteTransformer.Lz4();
        var source = Encoding.UTF8.GetBytes(new string('a', 4096));

        var compressed = transformer.TransformWrite(source);
        (compressed.Length < source.Length).ShouldBeTrue();
        transformer.TransformRead(compressed).ToArray().SequenceEqual(source).ShouldBeTrue();

        var exception = Should.Throw<CompressionException>(() =>
            transformer.TransformRead(new byte[] { 1, 2, 3, 4 })
        );
        transformer.IsRecoverableReadException(exception).ShouldBeTrue();
    }

    [Test]
    public void Zstandard_RoundTripsAndRejectsMalformedInput()
    {
        var transformer = CompressionStateByteTransformer.Zstandard();
        var source = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("zstd ", 2048)));

        var compressed = transformer.TransformWrite(source);
        (compressed.Length < source.Length).ShouldBeTrue();
        transformer.TransformRead(compressed).ToArray().SequenceEqual(source).ShouldBeTrue();

        var exception = Should.Throw<CompressionException>(() =>
            transformer.TransformRead(new byte[] { 9, 9, 9, 9 })
        );
        transformer.IsRecoverableReadException(exception).ShouldBeTrue();
    }

    [Test]
    public void Constructor_RejectsUnknownAlgorithmAndLevels()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new CompressionStateByteTransformer((CompressionAlgorithm)42)
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            CompressionStateByteTransformer.Zstandard(99)
        );
    }
}
