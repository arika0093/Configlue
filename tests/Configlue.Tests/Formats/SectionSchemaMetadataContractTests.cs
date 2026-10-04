using System.Text;
using Configlue.Codecs;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class SectionSchemaMetadataContractTests
{
    private static readonly StateSchemaMetadata LogicalSectionSchema = new(
        "logical-section-model",
        3
    );
    private static readonly StateSchemaMetadata ContainerSchema = new(
        "container-document-model",
        5
    );
    private static readonly StateSchemaMetadata OtherContainerSchema = new(
        "other-container-model",
        1
    );

    [Test]
    public async Task JsonSectionWrite_DoesNotStampLogicalSchemaOnPhysicalDocument()
    {
        var resource = new InMemoryResource();
        var section = new JsonSectionResource(resource, "App:Settings");

        await section.WriteAsync(
            new ResourceWriteRequest("""{"value":1}"""u8.ToArray(), Schema: LogicalSectionSchema)
        );

        (await resource.ReadAsync()).Schema.ShouldBeNull();
        using var sectionDocument = System.Text.Json.JsonDocument.Parse(
            (await section.ReadAsync()).Content
        );
        sectionDocument.RootElement.GetProperty("value").GetInt32().ShouldBe(1);
    }

    [Test]
    public async Task YamlSectionWrite_DoesNotStampLogicalSchemaOnPhysicalDocument()
    {
        var resource = new InMemoryResource();
        var section = new YamlSectionResource(resource, "App:Settings");

        await section.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes("value: 1\n"),
                Schema: LogicalSectionSchema
            )
        );

        (await resource.ReadAsync()).Schema.ShouldBeNull();
    }

    [Test]
    public async Task XmlSectionWrite_DoesNotStampLogicalSchemaOnPhysicalDocument()
    {
        var resource = new InMemoryResource();
        var section = new XmlSectionResource(resource, "App:Settings");

        await section.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes("<value>1</value>"),
                Schema: LogicalSectionSchema
            )
        );

        (await resource.ReadAsync()).Schema.ShouldBeNull();
    }

    [Test]
    public async Task SectionWrite_PreservesExistingContainerSchema()
    {
        var resource = new InMemoryResource();
        await resource.WriteAsync(
            new ResourceWriteRequest(
                """{"App":{"Settings":{"value":0}}}"""u8.ToArray(),
                Schema: ContainerSchema
            )
        );
        var section = new JsonSectionResource(resource, "App:Settings");

        await section.WriteAsync(
            new ResourceWriteRequest("""{"value":1}"""u8.ToArray(), Schema: LogicalSectionSchema)
        );

        (await resource.ReadAsync()).Schema.ShouldBe(ContainerSchema);
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task SectionWrites_PreserveSchemaWithMetadataReplacingWriter(string format)
    {
        var physical = new MetadataReplacingResource();
        var (initial, first, second) = format switch
        {
            "json" => ("{\"App\":{\"Settings\":{\"value\":0}}}", "{\"value\":1}", "{\"value\":2}"),
            "yaml" => ("App:\n  Settings:\n    value: 0\n", "value: 1\n", "value: 2\n"),
            _ => (
                "<Root><App><Settings><value>0</value></Settings></App></Root>",
                "<value>1</value>",
                "<value>2</value>"
            ),
        };
        await physical.WriteAsync(
            default,
            new ResourceWriteRequest(Encoding.UTF8.GetBytes(initial), Schema: ContainerSchema)
        );
        IResourceWriter section = format switch
        {
            "json" => new JsonSectionResource(physical, physical, "App:Settings"),
            "yaml" => new YamlSectionResource(physical, physical, "App:Settings"),
            _ => new XmlSectionResource(physical, physical, "App:Settings"),
        };
        await section.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes(first), Schema: LogicalSectionSchema)
        );
        (await physical.ReadAsync(default)).Schema.ShouldBe(ContainerSchema);
        await section.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes(second), Schema: OtherContainerSchema)
        );
        (await physical.ReadAsync(default)).Schema.ShouldBe(ContainerSchema);
        (await ((IResourceReader)section).ReadAsync()).Schema.ShouldBeNull();
    }

    [Test]
    public async Task FileBatch_RejectsConflictingContainerSchemasBeforeTouchingFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "configlue-section-schema-" + Guid.NewGuid().ToString("N") + ".json"
        );
        var resource = new FileResource(path);
        var first = new JsonSectionResource(resource, "App:First")
        {
            ContainerSchema = ContainerSchema,
        };
        var second = new JsonSectionResource(resource, "App:Second")
        {
            ContainerSchema = OtherContainerSchema,
        };
        var mutations = new[]
        {
            first.CreateMutation(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest("{}"u8.ToArray())
            ),
            second.CreateMutation(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest("{}"u8.ToArray())
            ),
        };
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await resource.WriteBatchAsync(mutations)
        );
        File.Exists(path).ShouldBeFalse();
    }

    [Test]
    public async Task ExplicitContainerSchema_IsStampedForEveryProvider()
    {
        var jsonResource = new InMemoryResource();
        var jsonSection = new JsonSectionResource(jsonResource, "App:Settings")
        {
            ContainerSchema = ContainerSchema,
        };
        await jsonSection.WriteAsync(
            new ResourceWriteRequest("""{"value":1}"""u8.ToArray(), Schema: LogicalSectionSchema)
        );
        (await jsonResource.ReadAsync()).Schema.ShouldBe(ContainerSchema);

        var yamlResource = new InMemoryResource();
        var yamlSection = new YamlSectionResource(yamlResource, "App:Settings")
        {
            ContainerSchema = ContainerSchema,
        };
        await yamlSection.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes("value: 1\n"),
                Schema: LogicalSectionSchema
            )
        );
        (await yamlResource.ReadAsync()).Schema.ShouldBe(ContainerSchema);

        var xmlResource = new InMemoryResource();
        var xmlSection = new XmlSectionResource(xmlResource, "App:Settings")
        {
            ContainerSchema = ContainerSchema,
        };
        await xmlSection.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes("<value>1</value>"),
                Schema: LogicalSectionSchema
            )
        );
        (await xmlResource.ReadAsync()).Schema.ShouldBe(ContainerSchema);
    }

    [Test]
    public async Task RootView_ForwardsLogicalSchemaForJsonAndYaml()
    {
        var jsonResource = new InMemoryResource();
        var jsonRoot = JsonSectionResource.CreateRoot(
            jsonResource,
            jsonResource,
            watcher: null,
            serializerOptions: null,
            fixedResourceId: null,
            schemaShape: []
        );
        await jsonRoot.WriteAsync(
            new ResourceWriteRequest("""{"value":1}"""u8.ToArray(), Schema: LogicalSectionSchema)
        );
        (await jsonResource.ReadAsync()).Schema.ShouldBe(LogicalSectionSchema);

        var yamlResource = new InMemoryResource();
        var yamlRoot = YamlSectionResource.CreateRoot(
            yamlResource,
            yamlResource,
            watcher: null,
            fixedResourceId: null,
            textEncoding: null,
            schemaShape: []
        );
        await yamlRoot.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes("value: 1\n"),
                Schema: LogicalSectionSchema
            )
        );
        (await yamlResource.ReadAsync()).Schema.ShouldBe(LogicalSectionSchema);
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task MultiSectionBatch_WithoutContainerSchema_KeepsDocumentMetadataUnstamped(
        string format
    )
    {
        var resource = new InMemoryResource();
        var options = BuildSiblingRuntime(resource, firstSchema: null, secondSchema: null, format);

        var result = await options.ApplyPatchesAsync([
            new StateSourcePatch(
                SourceId.From("first"),
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
            ),
            new StateSourcePatch(
                SourceId.From("second"),
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("second") }
            ),
        ]);

        result.PhysicalWriteCount.ShouldBe(1);
        resource.WriteCount.ShouldBe(1);
        (await resource.ReadAsync()).Schema.ShouldBeNull();
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task MultiSectionBatch_WithMatchingContainerSchema_StampsThatSchemaOnce(
        string format
    )
    {
        var resource = new InMemoryResource();
        var options = BuildSiblingRuntime(
            resource,
            firstSchema: ContainerSchema,
            secondSchema: ContainerSchema,
            format
        );

        var result = await options.ApplyPatchesAsync([
            new StateSourcePatch(
                SourceId.From("first"),
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
            ),
            new StateSourcePatch(
                SourceId.From("second"),
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("second") }
            ),
        ]);

        result.PhysicalWriteCount.ShouldBe(1);
        (await resource.ReadAsync()).Schema.ShouldBe(ContainerSchema);
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task MultiSectionBatch_WithConflictingContainerSchemas_IsRejectedBeforeWriting(
        string format
    )
    {
        var resource = new InMemoryResource();
        var options = BuildSiblingRuntime(
            resource,
            firstSchema: ContainerSchema,
            secondSchema: OtherContainerSchema,
            format
        );

        await Should.ThrowAsync<NotSupportedException>(async () =>
            await options.ApplyPatchesAsync([
                new StateSourcePatch(
                    SourceId.From("first"),
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
                ),
                new StateSourcePatch(
                    SourceId.From("second"),
                    new AppSettings.Patch { Label = FragmentOperation<string?>.Set("second") }
                ),
            ])
        );

        resource.WriteCount.ShouldBe(0);
        (await resource.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public void ResolveBatchSchema_TreatsNullAsNoClaimAndRejectsConflicts()
    {
        var first = CreateNullScopeMutation();
        var second = CreateNullScopeMutation();

        ResourceWriteMutation.ResolveBatchSchema([first]).ShouldBeNull();
        ResourceWriteMutation.ResolveBatchSchema([first, second]).ShouldBeNull();

        var stamped = CreateSchemaMutation(ContainerSchema);
        ResourceWriteMutation.ResolveBatchSchema([first, stamped]).ShouldBe(ContainerSchema);
        ResourceWriteMutation.ResolveBatchSchema([stamped, stamped]).ShouldBe(ContainerSchema);

        Should
            .Throw<NotSupportedException>(() =>
                ResourceWriteMutation.ResolveBatchSchema([
                    stamped,
                    CreateSchemaMutation(OtherContainerSchema),
                ])
            )
            .Message.ShouldContain("conflicting container schema metadata");
    }

    private sealed class MetadataReplacingResource : IResourceReader, IResourceWriter
    {
        private readonly InMemoryResource _content = new();
        private StateSchemaMetadata? _schema;

        public async ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            var result = await _content.ReadAsync(context, cancellationToken);
            return result.Status == StateReadStatus.Success
                ? ResourceReadResult.Success(result.Content, result.Revision, _schema)
                : result.Status switch
                {
                    StateReadStatus.NotFound => ResourceReadResult.NotFound(result.Revision),
                    StateReadStatus.Unavailable => ResourceReadResult.Unavailable(result.Revision),
                    StateReadStatus.InvalidPayload => ResourceReadResult.InvalidPayload(
                        result.Revision
                    ),
                    _ => throw new InvalidOperationException(
                        "Unexpected non-success resource status."
                    ),
                };
        }

        public async ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            ResourceWriteRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var result = await _content.WriteAsync(context, request, cancellationToken);
            _schema = request.Schema;
            return result;
        }
    }

    private static ConfiglueRuntime<AppSettings, AppSettings.Fragment> BuildSiblingRuntime(
        InMemoryResource resource,
        StateSchemaMetadata? firstSchema,
        StateSchemaMetadata? secondSchema,
        string format
    )
    {
        IStateCodec<AppSettings.Fragment> codec = format switch
        {
            "json" => new JsonStateCodec<AppSettings.Fragment>(),
            "yaml" => new YamlStateCodec<AppSettings.Fragment>(
                modelSchema: AppSettings.FragmentSchema
            ),
            _ => new XmlStateCodec<AppSettings.Fragment>(),
        };
        IResourceReader Section(string path, StateSchemaMetadata? schema) =>
            format switch
            {
                "json" => new JsonSectionResource(resource, path) { ContainerSchema = schema },
                "yaml" => new YamlSectionResource(resource, path) { ContainerSchema = schema },
                _ => new XmlSectionResource(resource, path) { ContainerSchema = schema },
            };
        IResourceReader firstSection = Section("App:First", firstSchema);
        var first = new StateSource<AppSettings.Fragment>(
            "first",
            new SerializedSource<AppSettings.Fragment>(
                firstSection,
                codec,
                writer: firstSection as IResourceWriter,
                watcher: firstSection as ISourceWatcher
            ),
            new StateSourceOptions<AppSettings.Fragment> { Priority = 10 }
        );
        IResourceReader secondSection = Section("App:Second", secondSchema);
        var second = new StateSource<AppSettings.Fragment>(
            "second",
            new SerializedSource<AppSettings.Fragment>(
                secondSection,
                codec,
                writer: secondSection as IResourceWriter,
                watcher: secondSection as ISourceWatcher
            ),
            new StateSourceOptions<AppSettings.Fragment> { Priority = 0 }
        );
        return new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([first, second]),
            StateWritePlan.DefaultTo(SourceId.From("first"))
        );
    }

    private static ResourceWriteMutation CreateNullScopeMutation() =>
        new(
            RevisionCondition.None,
            null,
            _ => "{}"u8.ToArray(),
            scope: "json/value",
            canCompose: true
        );

    private static ResourceWriteMutation CreateSchemaMutation(StateSchemaMetadata schema) =>
        new(
            RevisionCondition.None,
            schema,
            _ => "{}"u8.ToArray(),
            scope: "json/value",
            canCompose: true
        );
}
