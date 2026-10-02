using System.Text;
using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class ResourceBatchParticipantContractTests
{
    [Test]
    public async Task IResourceBatchParticipant_PreparesScopedMutationForItsPhysicalWriter()
    {
        var resource = new InMemoryResource();
        var initialContent = """
            {"App":{"Settings":{"Value":1},"Sibling":"preserve"}}
            """;
        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes(initialContent)));
        var schema = new StateSchemaMetadata("settings", 2);
        var section = new JsonSectionResource(resource, "App:Settings")
        {
            ContainerSchema = schema,
        };
        IResourceBatchParticipant participant = section;
        var sectionBeforeWrite = await section.ReadAsync();

        var suppressedMutation = (
            (IResourceBatchParticipant)new JsonSectionResource(resource, "App:Settings")
        ).CreateMutation(
            new ResourceWriteRequest(
                """{"Value":2}"""u8.ToArray(),
                Condition: RevisionCondition.FromRevision(sectionBeforeWrite.Revision),
                Schema: schema
            )
        );
        suppressedMutation.Schema.ShouldBeNull();

        var mutation = participant.CreateMutation(
            new ResourceWriteRequest(
                """{"Value":2}"""u8.ToArray(),
                Condition: RevisionCondition.FromRevision(sectionBeforeWrite.Revision),
                Schema: schema
            )
        );

        participant.ResourceId.ShouldBe(resource.ResourceId);
        participant.BatchWriter.ShouldBeSameAs(resource);
        mutation.Condition.Revision.ShouldBe(sectionBeforeWrite.Revision);
        mutation.Condition.ShouldBe(RevisionCondition.Match(sectionBeforeWrite.Revision!));
        mutation.Schema.ShouldBe(schema);
        mutation.Scope.ShouldBe("json/App/Settings");
        mutation.CanCompose.ShouldBeTrue();

        var physicalWriter = participant.BatchWriter!;
        var writeResult = await physicalWriter.WriteBatchAsync([mutation]);
        var updatedSection = await section.ReadAsync();
        var updatedResource = await resource.ReadAsync();

        writeResult.Revision.ShouldBe(updatedResource.Revision);
        updatedResource.Schema.ShouldBe(schema);
        Encoding.UTF8.GetString(updatedSection.Content.Span).ShouldBe("""{"Value":2}""");
        Encoding
            .UTF8.GetString(updatedResource.Content.Span)
            .ShouldContain("\"Sibling\":\"preserve\"");
    }
}
