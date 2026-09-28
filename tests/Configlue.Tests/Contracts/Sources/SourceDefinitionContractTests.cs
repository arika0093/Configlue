using Configlue.Testing;

namespace Configlue.Tests;

public sealed class SourceDefinitionContractTests
{
    [Test]
    public void SourceCreationFailureDisposesResourcesAllocatedBeforeTheFailure()
    {
        var resource = new DisposableProbe();
        Should.Throw<InvalidOperationException>(() =>
            ConfiglueApp.CreateContext(builder =>
                builder.Add<AppSettings>(model =>
                    model.Sources(sources => sources.Add(new FailingSourceDefinition(resource)))
                )
            )
        );
        resource.DisposeCallCount.ShouldBe(1);
    }

    [Test]
    public async Task SourceCreationResultKeepsApplicationResourcesBorrowed()
    {
        var resource = new DisposableProbe();
        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(new BorrowedSourceDefinition(resource)))
            )
        );
        await context.DisposeAsync();
        resource.DisposeCallCount.ShouldBe(0);
    }

    private sealed class FailingSourceDefinition(DisposableProbe resource)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            context.Own(resource);
            throw new InvalidOperationException("Provider creation failed.");
        }
    }

    private sealed class BorrowedSourceDefinition(DisposableProbe resource)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            _ = resource;
            var store = new InMemoryStateStore<TFragment>();
            return new ConfiglueSourceCreation<TFragment>(
                new StateSource<TFragment>("borrowed", store)
            );
        }
    }

    [Test]
    public async Task IConfiglueSourceDefinition_ReportsHelperCreatedResourcesToTheContext()
    {
        var ownedResource = new DisposableProbe();
        var definition = new ProbeSourceDefinition(ownedResource);
        await using var context = ConfiglueApp.CreateContext(builder =>
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

        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            return context.Complete(CreateSourceCore<TFragment>(context.ModelSchema, context.Own));
        }

        private StateSource<TFragment> CreateSourceCore<TFragment>(
            ConfiglueModelSchema modelSchema,
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
