using System.Buffers;
using System.Text;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class InMemoryResourceContractTests
{
    [Test]
    public async Task IResourceReaderAndWriter_RoundTripContentAndEnforceRevisions()
    {
        var resource = new InMemoryResource();
        IResourceReader reader = resource;
        IResourceWriter writer = resource;
        var initial = await reader.ReadAsync();

        initial.Status.ShouldBe(StateReadStatus.NotFound);
        var written = await writer.WriteAsync(new ResourceWriteRequest("first"u8.ToArray()));
        var current = await reader.ReadAsync();

        current.Status.ShouldBe(StateReadStatus.Success);
        Encoding.UTF8.GetString(current.Content.Span).ShouldBe("first");
        current.Revision.ShouldBe(written.Revision);
        await Should.ThrowAsync<StateConflictException>(async () =>
            await writer.WriteAsync(
                new ResourceWriteRequest(
                    "stale"u8.ToArray(),
                    ExpectedRevision: initial.Revision,
                    CheckRevision: true
                )
            )
        );
        Encoding.UTF8.GetString((await reader.ReadAsync()).Content.Span).ShouldBe("first");
    }

    [Test]
    public async Task IResourceBatchWriter_ComposesScopedMutationsIntoOnePhysicalWrite()
    {
        var resource = new InMemoryResource();
        IResourceBatchWriter batchWriter = resource;
        IResourceReader reader = resource;
        var initialWrite = await resource.WriteAsync(new ResourceWriteRequest("base"u8.ToArray()));
        var mutations = new[]
        {
            new ResourceWriteMutation(
                initialWrite.Revision,
                checkRevision: true,
                schema: null,
                _ => " first"u8.ToArray(),
                scope: "json/first",
                canCompose: true
            ),
            new ResourceWriteMutation(
                initialWrite.Revision,
                checkRevision: true,
                schema: null,
                current =>
                    Encoding.UTF8.GetBytes(
                        Encoding.UTF8.GetString(current.Content.Span) + " second"
                    ),
                scope: "json/second",
                canCompose: true
            ),
        };

        var result = await batchWriter.WriteBatchAsync(mutations);
        var stored = await reader.ReadAsync();

        resource.WriteCount.ShouldBe(2);
        stored.Revision.ShouldBe(result.Revision);
        Encoding.UTF8.GetString(stored.Content.Span).ShouldBe(" first second");
    }

    [Test]
    public async Task IResourceIdentityAndPipelineReader_ExposeStableResourceCapabilities()
    {
        var resource = new InMemoryResource();
        IResourceIdentity identity = resource;
        IPipelineResourceReader pipelineReader = resource;
        await resource.WriteAsync(new ResourceWriteRequest("payload"u8.ToArray()));

        identity.ResourceId.ShouldBe(resource.ResourceId);
        pipelineReader.IsPipelineReadPreferred.ShouldBeFalse();
        await using var result = await pipelineReader.ReadPipelineAsync();
        result.Status.ShouldBe(StateReadStatus.Success);
        var content = await result.ReadAllAsync();
        Encoding.UTF8.GetString(content.ToArray()).ShouldBe("payload");
    }
}
