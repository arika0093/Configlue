using Configlue.Testing;

namespace Configlue.Tests;

public sealed class SourceDefinitionContractTests
{
    [Test]
    public async Task IConfiglueSourceDefinition_ReportsHelperCreatedResourcesToTheContext()
    {
        var ownedResource = new DisposableProbe();
        var definition = new ProbeSourceDefinition(ownedResource);
        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model => model.Sources(sources => sources.Add(definition)));
        });

        await context.GetOptions<AppSettings>().GetValueAsync();

        definition.CreateCallCount.ShouldBe(1);
        definition.CreatedModelSchemaId.ShouldBe("app-settings");
        ownedResource.DisposeCallCount.ShouldBe(0);
        await context.DisposeAsync();

        ownedResource.DisposeCallCount.ShouldBe(1);
    }

    private sealed class ProbeSourceDefinition(DisposableProbe resource)
        : IConfiglueSourceDefinition
    {
        public int CreateCallCount { get; private set; }

        public string? CreatedModelSchemaId { get; private set; }

        public StateSource<TFragment> Create<TFragment>(
            ConfiglueModelSchema modelSchema,
            IServiceProvider? serviceProvider,
            Action<IDisposable> ownResource
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            CreateCallCount++;
            CreatedModelSchemaId = modelSchema.Id;
            ownResource(resource);
            var store = new InMemoryStateStore<TFragment>();
            return new StateSource<TFragment>("source-definition", store, writer: store);
        }
    }

    private sealed class DisposableProbe : IDisposable
    {
        public int DisposeCallCount { get; private set; }

        public void Dispose() => DisposeCallCount++;
    }
}
