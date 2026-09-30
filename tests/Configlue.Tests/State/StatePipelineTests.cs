using System.Security.Cryptography;
using Configlue.Provider.Json;
using Configlue.Sources;
using Configlue.Testing;
using Configlue.Transformer.AES;

namespace Configlue.Tests;

public sealed class StatePipelineTests
{
    [Test]
    public async Task FromResource_EncryptsBytesAndAppliesStateMiddleware()
    {
        var resource = new InMemoryResource();
        using var transformer = new AesGcmStateByteTransformer(new byte[32]);
        var source = SerializedStateSource.FromResource<string>(
            "encrypted",
            resource,
            new JsonStateCodec<string>(),
            transformers: [transformer],
            middlewares: [new SuffixMiddleware("!")]
        );

        await source.Writer!.WriteAsync(new StateWriteRequest<string>("sensitive"));
        var stored = await resource.ReadAsync();
        var read = await source.Reader.ReadAsync();

        stored.Status.ShouldBe(StateReadStatus.Success);
        System.Text.Encoding.UTF8.GetString(stored.Content.Span).ShouldNotContain("sensitive");
        read.Value.ShouldBe("sensitive!!");
    }

    [Test]
    public void AesGcmTransformer_RejectsTamperedContent()
    {
        using var transformer = new AesGcmStateByteTransformer(new byte[32]);
        var encrypted = transformer.TransformWrite("secret"u8.ToArray());
        var tampered = encrypted.ToArray();
        tampered[^1] ^= 0x01;

        Should.Throw<CryptographicException>(() => transformer.TransformRead(tampered));
    }

    [Test]
    public void AesGcmTransformer_RejectsInvalidKeys()
    {
        Should.Throw<ArgumentException>(() => new AesGcmStateByteTransformer(new byte[15]));
    }

    [Test]
    public void AesGcmPassphraseTransformer_RoundTripsAndRejectsTamperedOrWrongKeyContent()
    {
        using var writer = new AesGcmPassphraseStateByteTransformer("correct horse battery staple");
        using var reader = new AesGcmPassphraseStateByteTransformer("correct horse battery staple");
        using var wrongKey = new AesGcmPassphraseStateByteTransformer("different passphrase");
        var plaintext = "save data"u8.ToArray();

        var encrypted = writer.TransformWrite(plaintext);
        System.Text.Encoding.UTF8.GetString(encrypted.Span).ShouldNotContain("save data");
        reader.TransformRead(encrypted).ToArray().SequenceEqual(plaintext).ShouldBeTrue();
        Should.Throw<CryptographicException>(() => wrongKey.TransformRead(encrypted));

        var tampered = encrypted.ToArray();
        tampered[^1] ^= 0x01;
        Should.Throw<CryptographicException>(() => reader.TransformRead(tampered));
    }

    [Test]
    public void AesGcmPassphraseTransformer_RejectsEmptyPassphrases()
    {
        Should.Throw<ArgumentException>(() => new AesGcmPassphraseStateByteTransformer(""));
    }

    private sealed class SuffixMiddleware(string suffix) : IStateMiddleware<string>
    {
        public ISourceReader<string> WrapReader(ISourceReader<string> next) =>
            new SuffixReader(next, suffix);

        public ISourceWriter<string> WrapWriter(ISourceWriter<string> next) =>
            new SuffixWriter(next, suffix);
    }

    private sealed class SuffixReader(ISourceReader<string> next, string suffix)
        : ISourceReader<string>
    {
        public async ValueTask<StateReadResult<string>> ReadAsync(
            CancellationToken cancellationToken = default
        )
        {
            var result = await next.ReadAsync(cancellationToken);
            return result.Status == StateReadStatus.Success
                ? StateReadResult<string>.Success(
                    result.Value + suffix,
                    result.Revision,
                    result.Schema
                ) with
                {
                    SourceId = result.SourceId,
                    PhysicalOrigin = result.PhysicalOrigin,
                    Revisions = result.Revisions,
                }
                : result;
        }
    }

    private sealed class SuffixWriter(ISourceWriter<string> next, string suffix)
        : ISourceWriter<string>
    {
        public ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<string> request,
            CancellationToken cancellationToken = default
        ) => next.WriteAsync(request with { Value = request.Value + suffix }, cancellationToken);
    }
}
