using Configlue.Extensibility;
using Configlue.Provider.Json;
using Configlue.Provider.MessagePack;
using Configlue.State;
using Configlue.Testing;
using Configlue.Transformer.AES;
using Configlue.Transformer.Compression;

namespace Configlue.Tests;

public sealed class CompressionCompositionTests
{
    [Test]
    [Arguments(CompressionAlgorithm.Lz4, false)]
    [Arguments(CompressionAlgorithm.Zstandard, false)]
    [Arguments(CompressionAlgorithm.Lz4, true)]
    [Arguments(CompressionAlgorithm.Zstandard, true)]
    public async Task Compression_ComposesWithMessagePackAndAesThroughTransformerPipeline(
        CompressionAlgorithm algorithm,
        bool useJson
    )
    {
        var resource = new InMemoryResource();
        using var aes = new AesGcmStateByteTransformer(new byte[32]);
        var compression = new CompressionStateByteTransformer(algorithm);
        var fragment = MessagePackSampleSettings.Fragment.From(
            new MessagePackSampleSettings
            {
                Name = "composed",
                Items = Enumerable.Range(0, 64).Select(index => index.ToString()).ToList(),
            }
        );
        IStateCodec<MessagePackSampleSettings.Fragment> codec = useJson
            ? new JsonStateCodec<MessagePackSampleSettings.Fragment>()
            : new MessagePackStateCodec<MessagePackSampleSettings.Fragment>(
                TestMessagePack.Options
            );
        var source = new StateSource<MessagePackSampleSettings.Fragment>("composed", new SerializedSource<MessagePackSampleSettings.Fragment>(resource, codec, transformers: [aes, compression], writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<MessagePackSampleSettings.Fragment>());

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
