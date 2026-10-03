using Configlue.Extensibility;
using Configlue.Provider.Json;
using Configlue.Resources;

namespace Configlue.Tests;

public sealed class ResourceReadResultContractTests
{
    [Test]
    public void DefaultAndFactoriesOnlyExposeCanonicalReadOutcomes()
    {
        var missing = default(ResourceReadResult);
        missing.Status.ShouldBe(StateReadStatus.NotFound);
        missing.Content.IsEmpty.ShouldBeTrue();
        missing.Revision.ShouldBeNull();
        missing.Schema.ShouldBeNull();

        var success = ResourceReadResult.Success(
            "data"u8.ToArray(),
            "success",
            new StateSchemaMetadata("settings", 2)
        );
        success.Status.ShouldBe(StateReadStatus.Success);
        success.Content.ToArray().ShouldBe("data"u8.ToArray());
        success.Revision.ShouldBe("success");
        success.Schema.ShouldBe(new StateSchemaMetadata("settings", 2));
        var notFound = ResourceReadResult.NotFound("missing");
        notFound.Status.ShouldBe(StateReadStatus.NotFound);
        notFound.Content.IsEmpty.ShouldBeTrue();
        notFound.Revision.ShouldBe("missing");
        ResourceReadResult.Unavailable("offline").Status.ShouldBe(StateReadStatus.Unavailable);
        ResourceReadResult
            .InvalidPayload("malformed")
            .Status.ShouldBe(StateReadStatus.InvalidPayload);
        ((int)StateReadStatus.Success).ShouldBe(0);
        ((int)StateReadStatus.NotFound).ShouldBe(1);
        ((int)StateReadStatus.Unavailable).ShouldBe(2);
        ((int)StateReadStatus.InvalidPayload).ShouldBe(3);
        typeof(ResourceReadResult).GetConstructors().ShouldBeEmpty();
        ((int)StateReadStatus.Success).ShouldBe(0);
        ((int)StateReadStatus.NotFound).ShouldBe(1);
        ((int)StateReadStatus.Unavailable).ShouldBe(2);
        ((int)StateReadStatus.InvalidPayload).ShouldBe(3);
        typeof(ResourceReadResult)
            .GetProperty(nameof(ResourceReadResult.Status))!
            .SetMethod.ShouldBeNull();
        typeof(ResourceReadResult)
            .GetProperty(nameof(ResourceReadResult.Content))!
            .SetMethod.ShouldBeNull();
    }

    [Test]
    public async Task DefaultResourceProviderResultRemainsMissingThroughSerializedReader()
    {
        var reader = new SerializedStateReader<AppSettings.Fragment>(
            new DefaultResourceReader(),
            new JsonStateCodec<AppSettings.Fragment>()
        );

        var result = await reader.ReadAsync(ConfiglueResourceContext.Default);

        result.Status.ShouldBe(StateReadStatus.NotFound);
        result.Value.ShouldBeNull();
    }

    private sealed class DefaultResourceReader : IResourceReader
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => ValueTaskCompat.FromResult(default(ResourceReadResult));
    }
}
