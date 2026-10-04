using System.Buffers;
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
        var source = new StateSource<string>("encrypted", new SerializedSource<string>(resource, new JsonStateCodec<string>(), transformers: [transformer], writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher, middlewares: [new SuffixMiddleware("!")]), new StateSourceOptions<string>());

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
        var encrypted = new ArrayBufferWriter<byte>();
        transformer.TransformWrite("secret"u8, encrypted);
        var decrypted = new ArrayBufferWriter<byte>();
        transformer.TransformRead(encrypted.WrittenSpan, decrypted);
        System.Text.Encoding.UTF8.GetString(decrypted.WrittenSpan).ShouldBe("secret");
        var tampered = encrypted.WrittenSpan.ToArray();
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

        var destinationEncrypted = new ArrayBufferWriter<byte>();
        writer.TransformWrite(plaintext, destinationEncrypted);
        var destinationPlaintext = new ArrayBufferWriter<byte>();
        reader.TransformRead(destinationEncrypted.WrittenSpan, destinationPlaintext);
        destinationPlaintext.WrittenSpan.SequenceEqual(plaintext).ShouldBeTrue();
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

    private sealed class SuffixMiddleware(string suffix)
        : IStateReaderMiddleware<string>, IStateWriterMiddleware<string>
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
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            var result = await next.ReadAsync(context, cancellationToken);
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
            ConfiglueResourceContext context,
            StateWriteRequest<string> request,
            CancellationToken cancellationToken = default
        ) =>
            next.WriteAsync(
                context,
                new StateWriteRequest<string>(request.Value + suffix, request.Condition),
                cancellationToken
            );
    }
}
