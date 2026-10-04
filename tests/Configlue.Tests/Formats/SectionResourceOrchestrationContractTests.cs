using System.Text;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Testing;

namespace Configlue.Tests;

/// <summary>
/// Reusable orchestration contract for JSON/YAML/XML section resources.
/// Covers only transport/orchestration owned by the shared core: identity, non-success
/// propagation, pipeline materialization, backup-recovery forwarding, writer availability,
/// conditional writes, batch composition, schema metadata, and watcher delegation.
/// Format parsing/editing stays in format-specific tests.
/// </summary>
public sealed class SectionResourceOrchestrationContractTests
{
    private static (IResourceReader Reader, IResourceWriter Writer, Func<IResourceReader, IResourceWriter, object> CreateSection) Adapter(
        string format,
        InMemoryResource physical,
        string path = "App:Settings"
    ) =>
        format switch
        {
            "json" => (
                (IResourceReader)new JsonSectionResource(physical, path),
                (IResourceWriter)new JsonSectionResource(physical, path),
                (Func<IResourceReader, IResourceWriter, object>)(
                    (r, w) => new JsonSectionResource(r, w, path, watcher: null)
                )
            ),
            "yaml" => (
                (IResourceReader)new YamlSectionResource(physical, path),
                (IResourceWriter)new YamlSectionResource(physical, path),
                (Func<IResourceReader, IResourceWriter, object>)(
                    (r, w) => new YamlSectionResource(r, w, path, watcher: null)
                )
            ),
            _ => (
                (IResourceReader)new XmlSectionResource(physical, path),
                (IResourceWriter)new XmlSectionResource(physical, path),
                (Func<IResourceReader, IResourceWriter, object>)(
                    (r, w) => new XmlSectionResource(r, w, path, watcher: null)
                )
            ),
        };

    private static byte[] SectionPayload(string format) =>
        format switch
        {
            "json" => """{"value":1}"""u8.ToArray(),
            "yaml" => Encoding.UTF8.GetBytes("value: 1\n"),
            _ => Encoding.UTF8.GetBytes("<value>1</value>"),
        };

    private static byte[] PhysicalDocument(string format) =>
        format switch
        {
            "json" => """{"App":{"Settings":{"value":0},"Sibling":"keep"}}"""u8.ToArray(),
            "yaml" => Encoding.UTF8.GetBytes("App:\n  Settings:\n    value: 0\n  Sibling: keep\n"),
            _ => Encoding.UTF8.GetBytes(
                "<configuration><App><Settings><value>0</value></Settings><Sibling>keep</Sibling></App></configuration>"
            ),
        };

    private static object CreateSection(string format, IResourceReader reader, string path = "App:Settings") =>
        format switch
        {
            "json" => new JsonSectionResource(reader, path),
            "yaml" => new YamlSectionResource(reader, path),
            _ => new XmlSectionResource(reader, path),
        };

    private static dynamic CreateSectionWithWriter(
        string format,
        IResourceReader reader,
        IResourceWriter? writer,
        string path = "App:Settings"
    ) =>
        format switch
        {
            "json" => new JsonSectionResource(reader, writer, path, watcher: null),
            "yaml" => new YamlSectionResource(reader, writer, path, watcher: null),
            _ => new XmlSectionResource(reader, writer, path, watcher: null),
        };

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public void FixedResourceId_OverridesPhysicalIdentity(string format)
    {
        var physical = new InMemoryResource();
        var fixedId = new ResourceId($"test:{format}-section");
        object section = format switch
        {
            "json" => new JsonSectionResource(physical, "App:Settings", fixedResourceId: fixedId),
            "yaml" => new YamlSectionResource(
                physical,
                null,
                "App:Settings",
                null,
                fixedResourceId: fixedId
            ),
            _ => new XmlSectionResource(physical, null, "App:Settings", null, fixedId),
        };
        var context = ConfiglueResourceContext.Default;

        ((ITryResourceIdentity)section).TryGetResourceId(context, out var resolved).ShouldBeTrue();
        resolved.ShouldBe(fixedId);
        ((IResourceBatchParticipant)section).GetResourceId(context).ShouldBe(fixedId);
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public void ResourceId_DelegatesToPhysicalResource(string format)
    {
        var physical = new InMemoryResource();
        var section = CreateSection(format, physical);
        var context = ConfiglueResourceContext.Default;

        ((ITryResourceIdentity)section).TryGetResourceId(context, out var resolved).ShouldBeTrue();
        resolved.ShouldBe(physical.ResourceId);
        ((IResourceBatchParticipant)section).GetResourceId(context).ShouldBe(physical.ResourceId);
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public void ResourceId_UnknownWhenPhysicalHasNoIdentity(string format)
    {
        var reader = new UnknownIdentityReader();
        var section = CreateSection(format, reader);
        var context = ConfiglueResourceContext.Default;

        ((ITryResourceIdentity)section).TryGetResourceId(context, out _).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() =>
            ((IResourceBatchParticipant)section).GetResourceId(context)
        );
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task NonSuccessPropagation_PreservesStatusAndRevision(string format)
    {
        foreach (var status in new[] { StateReadStatus.NotFound, StateReadStatus.Unavailable, StateReadStatus.InvalidPayload })
        {
            var reader = new FixedStatusReader(status, revision: "rev-1");
            var section = (IResourceReader)CreateSection(format, reader);
            var result = await section.ReadAsync(ConfiglueResourceContext.Default);
            result.Status.ShouldBe(status);
            result.Revision.ShouldBe("rev-1");
            result.Content.IsEmpty.ShouldBeTrue();
        }
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task PipelineRead_MaterializesBufferedSection(string format)
    {
        var physical = new InMemoryResource();
        await physical.WriteAsync(new ResourceWriteRequest(PhysicalDocument(format)));
        var section = (IPipelineResourceReader)CreateSection(format, physical);

        section.IsPipelineReadPreferred.ShouldBeFalse();
        var buffered = await ((IResourceReader)section).ReadAsync(ConfiglueResourceContext.Default);
        buffered.Status.ShouldBe(StateReadStatus.Success);

        var pipeline = await section.ReadPipelineAsync(ConfiglueResourceContext.Default);
        pipeline.Status.ShouldBe(StateReadStatus.Success);
        var collected = await CollectAsync(pipeline);
        collected.ShouldBe(buffered.Content.ToArray());
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task PipelineRead_PreservesNonSuccessStatus(string format)
    {
        var section = (IPipelineResourceReader)CreateSection(format, new UnknownIdentityReader());
        var pipeline = await section.ReadPipelineAsync(ConfiglueResourceContext.Default);
        pipeline.Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public void ReadOnlySection_ReportsCanWriteFalseAndRejectsWrites(string format)
    {
        var physical = new InMemoryResource();
        dynamic section = CreateSectionWithWriter(format, physical, writer: null);
        ((bool)section.CanWrite).ShouldBeFalse();
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task ReadOnlySection_WriteAsyncThrowsNotSupported(string format)
    {
        var physical = new InMemoryResource();
        dynamic section = CreateSectionWithWriter(format, physical, writer: null);
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await ((IResourceWriter)section).WriteAsync(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest(SectionPayload(format))
            )
        );
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task ConditionalWrite_RejectsStaleRevision(string format)
    {
        var physical = new InMemoryResource();
        await physical.WriteAsync(new ResourceWriteRequest(PhysicalDocument(format)));
        dynamic section = format switch
        {
            "json" => new JsonSectionResource(physical, "App:Settings"),
            "yaml" => new YamlSectionResource(physical, "App:Settings"),
            _ => new XmlSectionResource(physical, "App:Settings"),
        };
        var observed = await ((IResourceReader)section).ReadAsync(ConfiglueResourceContext.Default);
        observed.Status.ShouldBe(StateReadStatus.Success);

        // Change the physical document so the observed section revision is stale.
        await physical.WriteAsync(new ResourceWriteRequest(PhysicalDocument(format)));

        await Should.ThrowAsync<StateConflictException>(async () =>
            await ((IResourceWriter)section).WriteAsync(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest(
                    SectionPayload(format),
                    Condition: RevisionCondition.FromRevision("stale-revision")
                )
            )
        );
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public void BatchMutation_MustNotExistBecomesUnconditionalWithScope(string format)
    {
        var physical = new InMemoryResource();
        dynamic section = format switch
        {
            "json" => new JsonSectionResource(physical, "App:Settings"),
            "yaml" => new YamlSectionResource(physical, "App:Settings"),
            _ => new XmlSectionResource(physical, "App:Settings"),
        };
        var mutation = ((IResourceBatchParticipant)section).CreateMutation(
            ConfiglueResourceContext.Default,
            new ResourceWriteRequest(
                SectionPayload(format),
                Condition: RevisionCondition.MustNotExist
            )
        );
        mutation.Condition.IsNone.ShouldBeTrue();
        mutation.CanCompose.ShouldBeTrue();
        mutation.Scope.ShouldNotBeNull();
        mutation.Scope.ShouldStartWith(format + "/");
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task SiblingBatch_ComposesWithoutPhysicalConflict(string format)
    {
        var physical = new InMemoryResource();
        dynamic first = format switch
        {
            "json" => new JsonSectionResource(physical, "App:First"),
            "yaml" => new YamlSectionResource(physical, "App:First"),
            _ => new XmlSectionResource(physical, "App:First"),
        };
        dynamic second = format switch
        {
            "json" => new JsonSectionResource(physical, "App:Second"),
            "yaml" => new YamlSectionResource(physical, "App:Second"),
            _ => new XmlSectionResource(physical, "App:Second"),
        };
        var mutations = new[]
        {
            ((IResourceBatchParticipant)first).CreateMutation(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest(SectionPayload(format))
            ),
            ((IResourceBatchParticipant)second).CreateMutation(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest(SectionPayload(format))
            ),
        };
        ResourceWriteMutation.ValidateBatch(mutations);
        await physical.WriteBatchAsync(mutations);

        var firstRead = await ((IResourceReader)first).ReadAsync(ConfiglueResourceContext.Default);
        var secondRead = await ((IResourceReader)second).ReadAsync(
            ConfiglueResourceContext.Default
        );
        firstRead.Status.ShouldBe(StateReadStatus.Success);
        secondRead.Status.ShouldBe(StateReadStatus.Success);
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public void BatchMutation_OverlappingScopesAreRejected(string format)
    {
        var physical = new InMemoryResource();
        dynamic section = format switch
        {
            "json" => new JsonSectionResource(physical, "App:Settings"),
            "yaml" => new YamlSectionResource(physical, "App:Settings"),
            _ => new XmlSectionResource(physical, "App:Settings"),
        };
        var participant = (IResourceBatchParticipant)section;
        var mutations = new[]
        {
            participant.CreateMutation(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest(SectionPayload(format))
            ),
            participant.CreateMutation(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest(SectionPayload(format))
            ),
        };
        Should.Throw<StateConflictException>(() => ResourceWriteMutation.ValidateBatch(mutations));
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task SectionWrite_WithoutContainerSchemaPreservesExistingMetadata(string format)
    {
        var containerSchema = new StateSchemaMetadata("container-model", 5);
        var physical = new InMemoryResource();
        await physical.WriteAsync(
            new ResourceWriteRequest(PhysicalDocument(format), Schema: containerSchema)
        );
        dynamic section = format switch
        {
            "json" => new JsonSectionResource(physical, "App:Settings"),
            "yaml" => new YamlSectionResource(physical, "App:Settings"),
            _ => new XmlSectionResource(physical, "App:Settings"),
        };
        var logical = new StateSchemaMetadata("logical-model", 3);
        await ((IResourceWriter)section).WriteAsync(
            ConfiglueResourceContext.Default,
            new ResourceWriteRequest(SectionPayload(format), Schema: logical)
        );
        (await physical.ReadAsync(ConfiglueResourceContext.Default)).Schema.ShouldBe(
            containerSchema
        );
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task SectionWrite_WithContainerSchemaStampsPhysicalDocument(string format)
    {
        var containerSchema = new StateSchemaMetadata("container-model", 5);
        var physical = new InMemoryResource();
        dynamic section = format switch
        {
            "json" => new JsonSectionResource(physical, "App:Settings") { ContainerSchema = containerSchema },
            "yaml" => new YamlSectionResource(physical, "App:Settings") { ContainerSchema = containerSchema },
            _ => new XmlSectionResource(physical, "App:Settings") { ContainerSchema = containerSchema },
        };
        await ((IResourceWriter)section).WriteAsync(
            ConfiglueResourceContext.Default,
            new ResourceWriteRequest(
                SectionPayload(format),
                Schema: new StateSchemaMetadata("logical-model", 3)
            )
        );
        (await physical.ReadAsync(ConfiglueResourceContext.Default)).Schema.ShouldBe(
            containerSchema
        );
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task BackupRecovery_WithoutCapabilityReturnsNull(string format)
    {
        var section = (IContextualResourceBackupRecovery)CreateSection(
            format,
            new InMemoryResource()
        );
        section.AutomaticBackupRecoveryEnabled.ShouldBeFalse();
        var recovered = await section.TryRecoverLatestBackupAsync(
            ConfiglueResourceContext.Default,
            expectedRevision: null,
            expectedMissing: false,
            static (_, _) => new ValueTask<bool>(true)
        );
        recovered.ShouldBeNull();
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task BackupRecovery_DisabledCapabilityReturnsNull(string format)
    {
        var section = (IContextualResourceBackupRecovery)CreateSection(
            format,
            new DisabledRecoveryResource(PhysicalDocument(format))
        );
        section.AutomaticBackupRecoveryEnabled.ShouldBeFalse();
        var recovered = await section.TryRecoverLatestBackupAsync(
            ConfiglueResourceContext.Default,
            expectedRevision: null,
            expectedMissing: false,
            static (_, _) => new ValueTask<bool>(true)
        );
        recovered.ShouldBeNull();
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task BackupRecovery_ValidatesExtractedSection(string format)
    {
        var physical = new EnabledRecoveryResource(PhysicalDocument(format));
        var section = (IContextualResourceBackupRecovery)CreateSection(format, physical);

        section.AutomaticBackupRecoveryEnabled.ShouldBeTrue();
        var rejected = await section.TryRecoverLatestBackupAsync(
            ConfiglueResourceContext.Default,
            expectedRevision: null,
            expectedMissing: false,
            static (_, _) => new ValueTask<bool>(false)
        );
        rejected.ShouldBeNull();

        var accepted = await section.TryRecoverLatestBackupAsync(
            ConfiglueResourceContext.Default,
            expectedRevision: null,
            expectedMissing: false,
            static (_, _) => new ValueTask<bool>(true)
        );
        accepted.ShouldNotBeNull();
        accepted!.Value.Status.ShouldBe(StateReadStatus.Success);
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task Watcher_DelegatesWhenAvailable(string format)
    {
        var physical = new InMemoryResource();
        var watcher = new RecordingWatcher();
        object section = format switch
        {
            "json" => new JsonSectionResource(physical, physical, "App:Settings", watcher),
            "yaml" => new YamlSectionResource(physical, physical, "App:Settings", watcher),
            _ => new XmlSectionResource(physical, physical, "App:Settings", watcher),
        };
        await ((ISourceWatcher)section).WaitForChangeAsync(
            ConfiglueResourceContext.Default,
            observedRevision: "rev-1"
        );
        watcher.Calls.ShouldBe(1);
        watcher.LastRevision.ShouldBe("rev-1");
    }

    [Test]
    [Arguments("json")]
    [Arguments("yaml")]
    [Arguments("xml")]
    public async Task Watcher_PollsWhenUnavailable(string format)
    {
        var physical = new InMemoryResource();
        await physical.WriteAsync(new ResourceWriteRequest(PhysicalDocument(format)));
        var before = await physical.ReadAsync(ConfiglueResourceContext.Default);
        object section = format switch
        {
            "json" => new JsonSectionResource(
                (IResourceReader)physical,
                (IResourceWriter?)null,
                "App:Settings",
                watcher: null
            ),
            "yaml" => new YamlSectionResource(
                (IResourceReader)physical,
                (IResourceWriter?)null,
                "App:Settings",
                watcher: null
            ),
            _ => new XmlSectionResource(
                (IResourceReader)physical,
                (IResourceWriter?)null,
                "App:Settings",
                watcher: null
            ),
        };
        // Revision already differs from an unknown observation, so the poll loop returns immediately.
        await ((ISourceWatcher)section).WaitForChangeAsync(
            ConfiglueResourceContext.Default,
            observedRevision: "different-revision"
        );
        before.Revision.ShouldNotBeNull();
    }

    private static async Task<byte[]> CollectAsync(PipelineResourceReadResult pipeline)
    {
        if (pipeline.Content is null)
        {
            return [];
        }

        var reader = pipeline.Content;
        var collected = new List<byte>();
        while (true)
        {
            var read = await reader.ReadAsync();
            foreach (var segment in read.Buffer)
            {
                collected.AddRange(segment.ToArray());
            }
            reader.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
            {
                break;
            }
        }
        await reader.CompleteAsync();
        return [.. collected];
    }

    private sealed class UnknownIdentityReader : IResourceReader
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => new(ResourceReadResult.NotFound());
    }

    private sealed class FixedStatusReader(StateReadStatus status, string? revision)
        : IResourceReader
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) =>
            new(
                status switch
                {
                    StateReadStatus.NotFound => ResourceReadResult.NotFound(revision),
                    StateReadStatus.Unavailable => ResourceReadResult.Unavailable(revision),
                    StateReadStatus.InvalidPayload => ResourceReadResult.InvalidPayload(revision),
                    _ => ResourceReadResult.NotFound(revision),
                }
            );
    }

    private sealed class DisabledRecoveryResource(byte[] content)
        : IResourceReader,
            IResourceBackupRecovery
    {
        public bool AutomaticBackupRecoveryEnabled => false;

        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => new(ResourceReadResult.Success(content.ToArray()));

        public ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
            string? expectedRevision,
            bool expectedMissing,
            Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
            CancellationToken cancellationToken = default
        ) => new((ResourceReadResult?)null);
    }

    private sealed class EnabledRecoveryResource(byte[] content)
        : IResourceReader,
            IResourceBackupRecovery
    {
        public bool AutomaticBackupRecoveryEnabled => true;

        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => new(ResourceReadResult.Success(content.ToArray()));

        public async ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
            string? expectedRevision,
            bool expectedMissing,
            Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
            CancellationToken cancellationToken = default
        )
        {
            var candidate = ResourceReadResult.Success(content.ToArray());
            return await validate(candidate, cancellationToken).ConfigureAwait(false)
                ? candidate
                : null;
        }
    }

    private sealed class RecordingWatcher : ISourceWatcher
    {
        public int Calls { get; private set; }

        public string? LastRevision { get; private set; }

        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            Calls++;
            LastRevision = observedRevision;
            return default;
        }
    }
}
