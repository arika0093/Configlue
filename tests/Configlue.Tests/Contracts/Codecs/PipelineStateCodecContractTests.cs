using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Configlue.Provider.Json;

namespace Configlue.Tests;

public sealed class PipelineStateCodecContractTests
{
    [Test]
    public async Task IPipelineStateCodec_DecodesValuesAndResolvesSchemaMetadataPrecedence()
    {
        var codec = new JsonStateCodec<StrictJsonPipelineSettings>
        {
            UseAsyncStreamDecoding = true,
        };
        IPipelineStateCodec<StrictJsonPipelineSettings> pipelineCodec = codec;
        var contextSchema = new StateSchemaMetadata("context-model", 2);
        var payload = """
            {
              "$configlue": { "id": "payload-model", "version": 3 },
              "$value": { "Value": 17 }
            }
            """;

        pipelineCodec.IsPipelineDecodePreferred.ShouldBeTrue();
        var decoded = await DeserializeAsync(pipelineCodec, payload, contextSchema);
        var resourceSchema = new StateSchemaMetadata("resource-model", 4);
        var resourceOverride = await DeserializeAsync(
            pipelineCodec,
            payload,
            contextSchema,
            resourceSchema
        );

        decoded.Status.ShouldBe(StateReadStatus.Success);
        decoded.Value!.Value.ShouldBe(17);
        decoded.Schema.ShouldBe(new StateSchemaMetadata("payload-model", 3));
        resourceOverride.Schema.ShouldBe(resourceSchema);
        var contextFallback = await DeserializeAsync(
            pipelineCodec,
            """{"Value":19}""",
            contextSchema
        );
        contextFallback.Schema.ShouldBe(contextSchema);
        contextFallback.Value!.Value.ShouldBe(19);
    }

    [Test]
    public async Task SerializedStateReader_UsesPipelineOnlyWhenTheCodecPrefersIt()
    {
        const string payload = """{"Value":23}""";
        var preferredCodec = new JsonStateCodec<StrictJsonPipelineSettings>
        {
            UseAsyncStreamDecoding = true,
        };
        var preferredResource = new CountingPipelineResource(payload);
        var preferredReader = new SerializedStateReader<StrictJsonPipelineSettings>(
            preferredResource,
            preferredCodec
        );

        var preferredResult = await preferredReader.ReadAsync();

        preferredResult.Value!.Value.ShouldBe(23);
        preferredResource.PipelineReadCount.ShouldBe(1);
        preferredResource.MemoryReadCount.ShouldBe(0);

        var defaultCodec = new SequenceFallbackCodec();
        IPipelineStateCodec<StrictJsonPipelineSettings> pipelineContract = defaultCodec;
        pipelineContract.IsPipelineDecodePreferred.ShouldBeFalse();
        var defaultResource = new CountingPipelineResource(payload);
        var defaultReader = new SerializedStateReader<StrictJsonPipelineSettings>(
            defaultResource,
            defaultCodec
        );

        var defaultResult = await defaultReader.ReadAsync();

        defaultResult.Value!.Value.ShouldBe(23);
        // Measured (#295): the ReadAllAsync fallback never beats buffered reads, so ordinary
        // codecs stay buffered even when the resource prefers the pipeline.
        defaultResource.PipelineReadCount.ShouldBe(0);
        defaultResource.MemoryReadCount.ShouldBe(1);
        defaultCodec.PipelineDecodeCount.ShouldBe(0);
    }

    private static async Task<StateReadResult<StrictJsonPipelineSettings>> DeserializeAsync(
        IPipelineStateCodec<StrictJsonPipelineSettings> codec,
        string content,
        StateSchemaMetadata? contextSchema,
        StateSchemaMetadata? resourceSchema = null
    )
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var pipe = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            return await codec.DeserializeAsync(
                pipe,
                new StateCodecContext(contextSchema),
                resourceSchema
            );
        }
        finally
        {
            await pipe.CompleteAsync();
        }
    }

    private sealed class CountingPipelineResource(string content)
        : IResourceReader,
            IPipelineResourceReader
    {
        private readonly byte[] _content = Encoding.UTF8.GetBytes(content);

        public int MemoryReadCount { get; private set; }

        public int PipelineReadCount { get; private set; }

        public bool IsPipelineReadPreferred => true;

        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            MemoryReadCount++;
            return ValueTaskCompat.FromResult(ResourceReadResult.Success(_content));
        }

        public ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            PipelineReadCount++;
            var stream = new MemoryStream(_content, writable: false);
            var pipe = PipeReader.Create(stream);
            return ValueTaskCompat.FromResult(PipelineResourceReadResult.Success(pipe));
        }
    }

    private sealed class SequenceFallbackCodec
        : IStateCodec<StrictJsonPipelineSettings>,
            IPipelineStateCodec<StrictJsonPipelineSettings>
    {
        private readonly JsonStateCodec<StrictJsonPipelineSettings> _inner = new();

        public bool IsPipelineDecodePreferred => false;

        public int PipelineDecodeCount { get; private set; }

        public StrictJsonPipelineSettings? Deserialize(
            in ReadOnlySequence<byte> source,
            in StateCodecContext context
        ) => _inner.Deserialize(in source, in context);

        public void Serialize(
            StrictJsonPipelineSettings? value,
            IBufferWriter<byte> destination,
            in StateCodecContext context
        ) => _inner.Serialize(value, destination, in context);

        public async ValueTask<StateReadResult<StrictJsonPipelineSettings>> DeserializeAsync(
            PipeReader content,
            StateCodecContext context,
            StateSchemaMetadata? resourceSchema,
            CancellationToken cancellationToken = default
        )
        {
            PipelineDecodeCount++;
            return await _inner.DeserializeAsync(
                content,
                context,
                resourceSchema,
                cancellationToken
            );
        }
    }
}
