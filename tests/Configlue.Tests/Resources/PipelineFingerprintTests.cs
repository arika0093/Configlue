using System.Security.Cryptography;
using Configlue.Resources;

namespace Configlue.Tests;

public sealed class PipelineFingerprintTests
{
    [Test]
    public async Task FingerprintMatchesForManySmallStreamReads()
    {
        var content = Enumerable.Range(0, 256 * 1024).Select(static value => (byte)value).ToArray();
        string? fingerprint = null;
        await using var result = PipelineResourceReader.FromStream(
            new ChunkedMemoryStream(content, 127),
            contentFingerprintCompleted: value => fingerprint = value
        );

        var read = await result.ReadAllAsync();

        using var hash = SHA256.Create();
        read.Length.ShouldBe(content.Length);
        fingerprint.ShouldBe(Convert.ToHexString(hash.ComputeHash(content)));
    }

    private sealed class ChunkedMemoryStream(byte[] content, int chunkSize) : MemoryStream(content)
    {
        public override int Read(Span<byte> buffer) =>
            base.Read(buffer[..Math.Min(buffer.Length, chunkSize)]);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, chunkSize));

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => base.ReadAsync(buffer, offset, Math.Min(count, chunkSize), cancellationToken);
    }
}
