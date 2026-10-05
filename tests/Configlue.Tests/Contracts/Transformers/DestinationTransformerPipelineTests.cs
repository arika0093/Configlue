using System.Buffers;
using Configlue.State;
using Configlue.Transformer.AES;

namespace Configlue.Tests;

public sealed class DestinationTransformerPipelineTests
{
    [Test]
    [Arguments(0, false)]
    [Arguments(100, false)]
    [Arguments(4096, false)]
    [Arguments(65536, false)]
    [Arguments(0, true)]
    [Arguments(100, true)]
    [Arguments(4096, true)]
    [Arguments(65536, true)]
    public async Task ChunkedDestinationOutputsRoundTripAndRemainOwned(int length, bool mixedAsync)
    {
        var payload = Enumerable.Range(0, length).Select(static index => (byte)index).ToArray();
        var destination = new PrefixDestinationTransformer();
        var stages = StateByteTransformerPipeline.Create(
            mixedAsync ? [destination, new YieldTransformer()] : [destination]
        );
        var encoded = await StateByteTransformerPipeline.TransformWriteAsync(
            payload,
            stages,
            default
        );
        encoded.Length.ShouldBe(length + 1);
        encoded.Span[0].ShouldBe((byte)0x7f);
        encoded.Span[1..].SequenceEqual(payload).ShouldBeTrue();
        var retained = encoded.ToArray();

        var next = await StateByteTransformerPipeline.TransformWriteAsync(
            new byte[length],
            stages,
            default
        );
        encoded.Span.SequenceEqual(retained).ShouldBeTrue();
        next.Length.ShouldBe(length + 1);
        var decoded = await StateByteTransformerPipeline.TransformReadAsync(
            encoded,
            stages,
            default
        );
        decoded.Span.SequenceEqual(payload).ShouldBeTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(100)]
    [Arguments(65536)]
    public void AesDestinationWriteProducesDecryptableOutput(int length)
    {
        var payload = Enumerable.Range(0, length).Select(static index => (byte)index).ToArray();
        using var aes = new AesGcmStateByteTransformer(new byte[32]);
        var stages = StateByteTransformerPipeline.Create([aes]);
        var encoded = StateByteTransformerPipeline.TransformWrite(payload, stages);
        aes.TransformRead(encoded).Span.SequenceEqual(payload).ShouldBeTrue();
    }

    private sealed class PrefixDestinationTransformer : IDestinationStateByteTransformer
    {
        public ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source) =>
            throw new InvalidOperationException("The destination capability should be selected.");

        public ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source) =>
            throw new InvalidOperationException("The destination capability should be selected.");

        public void TransformRead(ReadOnlySpan<byte> source, IBufferWriter<byte> destination)
        {
            source[0].ShouldBe((byte)0x7f);
            source[1..].CopyTo(destination.GetSpan(source.Length - 1));
            destination.Advance(source.Length - 1);
        }

        public void TransformWrite(ReadOnlySpan<byte> source, IBufferWriter<byte> destination)
        {
            destination.GetSpan(1)[0] = 0x7f;
            destination.Advance(1);
            while (!source.IsEmpty)
            {
                var count = Math.Min(8192, source.Length);
                source[..count].CopyTo(destination.GetSpan(count));
                destination.Advance(count);
                source = source[count..];
            }
        }
    }

    private sealed class YieldTransformer : IAsyncStateByteTransformer
    {
        public async ValueTask<ReadOnlyMemory<byte>> TransformReadAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return source;
        }

        public ValueTask<ReadOnlyMemory<byte>> TransformWriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        ) => TransformReadAsync(source, cancellationToken);
    }
}
