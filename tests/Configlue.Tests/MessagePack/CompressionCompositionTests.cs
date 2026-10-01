using Configlue.Extensibility;
using Configlue.Provider.MessagePack;
using Configlue.State;
using Configlue.Testing;
using Configlue.Transformer.AES;
using Configlue.Transformer.Compression;

namespace Configlue.Tests;

public sealed class CompressionCompositionTests
{
    [Test]
    public async Task Compression_ComposesWithMessagePackAndAesThroughTransformerPipeline()
    {
        var resource = new InMemoryResource();
        using var aes = new AesGcmStateByteTransformer(new byte[32]);
        var compression = CompressionStateByteTransformer.Zstandard();
        var fragment = MessagePackSampleSettings.Fragment.From(
            new MessagePackSampleSettings
            {
                Name = "composed",
                Items = Enumerable.Range(0, 64).Select(index => index.ToString()).ToList(),
            }
        );
        var source = SerializedStateSource.FromResource<MessagePackSampleSettings.Fragment>(
            "composed",
            resource,
            new MessagePackStateCodec<MessagePackSampleSettings.Fragment>(TestMessagePack.Options),
            transformers: [aes, compression]
        );

        await source.Writer!.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<MessagePackSampleSettings.Fragment>(fragment)
        );
        var stored = await resource.ReadAsync(ConfiglueResourceContext.Default);
        var read = await source.Reader.ReadAsync(ConfiglueResourceContext.Default);

        stored.Status.ShouldBe(StateReadStatus.Success);
        stored.Content.Span.IndexOf("composed"u8).ShouldBe(-1);
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Name.Value.ShouldBe("composed");
        read.Value.Items.Value!.Count.ShouldBe(64);
    }
}
