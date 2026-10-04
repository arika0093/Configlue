using System.IO.Compression;
using System.Text;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Resource.Zip;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class StateOutcomeContractTests
{
    [Test]
    public void ReadFactories_CloseStatusAndValueAndKeepDefaultMissing()
    {
        default(StateReadResult<string>).Status.ShouldBe(StateReadStatus.NotFound);
        default(StateReadResult<int>).Status.ShouldBe(StateReadStatus.NotFound);
        default(StateReadResult<string>).ShouldBe(StateReadResult<string>.NotFound());
        Should.Throw<ArgumentNullException>(() => StateReadResult<string>.Success(null!));
        StateReadResult<int>.Success(0).Status.ShouldBe(StateReadStatus.Success);
        Should.Throw<ArgumentNullException>(() => StateReadResult<int?>.Success(null!));
        StateReadResult<int>.Success(default).Value.ShouldBe(0);
        StateReadResult<string>.NotFound("tombstone").Value.ShouldBeNull();
        StateReadResult<string>.Unavailable("unreachable").Value.ShouldBeNull();
        var invalid = StateReadResult<string>.InvalidPayload("invalid value", "revision");
        invalid.Status.ShouldBe(StateReadStatus.InvalidPayload);
        invalid.Value.ShouldBe("invalid value");
        invalid.FromSource(SourceId.From("source"), "origin").Status.ShouldBe(StateReadStatus.InvalidPayload);
        ((int)StateReadStatus.Success).ShouldBe(0);
        ((int)StateReadStatus.NotFound).ShouldBe(1);
        typeof(StateReadResult<string>).GetProperty("Status")!.SetMethod.ShouldBeNull();
        typeof(StateReadResult<string>).GetProperty("Value")!.SetMethod.ShouldBeNull();
    }

    [Test]
    public async Task InMemorySourceRejectsNullAtTheSetterBoundary()
    {
        Should.Throw<ArgumentNullException>(() => new InMemoryStateSource<string>(null!));
        var source = new InMemoryStateSource<string>("initial");
        Should.Throw<ArgumentNullException>(() => source.Set(null!));
        (await source.ReadAsync()).Value.ShouldBe("initial");

        var nullableValueSource = new InMemoryStateSource<int?>(1);
        Should.Throw<ArgumentNullException>(() => nullableValueSource.Set(null!));
        (await nullableValueSource.ReadAsync()).Value.ShouldBe(1);
    }

    [Test]
    public async Task StateWriteRequest_RejectsNullValuesAndCannotBeReinitialized()
    {
        Should.Throw<ArgumentNullException>(() => new StateWriteRequest<string>(null!));
        Should.Throw<ArgumentNullException>(() => new StateWriteRequest<int?>(null));
        default(StateWriteRequest<string>).ShouldBeNull();
        typeof(StateWriteRequest<string>).GetProperty(nameof(StateWriteRequest<string>.Value))!
            .SetMethod.ShouldBeNull();
        typeof(StateWriteRequest<string>).GetProperty(nameof(StateWriteRequest<string>.Condition))!
            .SetMethod.ShouldBeNull();

        var store = new InMemoryStateSource<string>();
        var source = new StateSource<string>("writer", store, new StateSourceOptions<string> { Writer = store });
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await source.WriteAsync(ConfiglueResourceContext.Default, null!)
        );
    }

    [Test]
    public async Task StateConditions_EnforceMatchAndAbsenceWithTombstoneRevisions()
    {
        var store = new InMemoryStateSource<string>();
        store.SetNotFound();
        (await store.ReadAsync()).Revision.ShouldNotBeNull();
        await store.WriteAsync(
            new StateWriteRequest<string>("created", RevisionCondition.MustNotExist)
        );
        await Should.ThrowAsync<StateConflictException>(async () =>
            await store.WriteAsync(
                new StateWriteRequest<string>("duplicate", RevisionCondition.MustNotExist)
            )
        );
        var observed = await store.ReadAsync();
        await store.WriteAsync(
            new StateWriteRequest<string>("matched", RevisionCondition.Match(observed.Revision!))
        );
        await Should.ThrowAsync<StateConflictException>(async () =>
            await store.WriteAsync(
                new StateWriteRequest<string>("stale", RevisionCondition.Match(observed.Revision!))
            )
        );
        await store.WriteAsync(new StateWriteRequest<string>("unchecked", RevisionCondition.None));
        (await store.ReadAsync()).Value.ShouldBe("unchecked");
        Should.Throw<ArgumentNullException>(() => RevisionCondition.Match(null!));
    }

    [Test]
    public async Task ResourceConditions_EnforceMatchAbsenceAndUncheckedWrites()
    {
        var resource = new InMemoryResource();
        await resource.WriteAsync(
            new ResourceWriteRequest("first"u8.ToArray(), RevisionCondition.MustNotExist)
        );
        var observed = await resource.ReadAsync();
        await resource.WriteAsync(
            new ResourceWriteRequest(
                "matched"u8.ToArray(),
                RevisionCondition.Match(observed.Revision!)
            )
        );
        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(
                    "stale"u8.ToArray(),
                    RevisionCondition.Match(observed.Revision!)
                )
            )
        );
        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest("duplicate"u8.ToArray(), RevisionCondition.MustNotExist)
            )
        );
        await resource.WriteAsync(
            new ResourceWriteRequest("unchecked"u8.ToArray(), RevisionCondition.None)
        );
        Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span).ShouldBe("unchecked");
    }

    [Test]
    public async Task BatchConditions_RejectDifferentSemanticsBeforeApplyingMutations()
    {
        var resource = new InMemoryResource();
        var applied = false;
        var mutations = new[]
        {
            new ResourceWriteMutation(
                RevisionCondition.None,
                null,
                _ =>
                {
                    applied = true;
                    return "first"u8.ToArray();
                },
                scope: "test/first",
                canCompose: true
            ),
            new ResourceWriteMutation(
                RevisionCondition.MustNotExist,
                null,
                _ => "second"u8.ToArray(),
                scope: "test/second",
                canCompose: true
            ),
        };
        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteBatchAsync(mutations)
        );
        applied.ShouldBeFalse();
        resource.WriteCount.ShouldBe(0);
    }

    [Test]
    public async Task ScopedResources_MustNotExistCreatesMissingSectionInExistingDocument()
    {
        await CheckSectionAsync(
            "{}",
            "{\"Value\":1}",
            static resource => new JsonSectionResource(resource, "Settings")
        );
        await CheckSectionAsync(
            "<Root />",
            "<Value>1</Value>",
            static resource => new XmlSectionResource(resource, "Settings")
        );
        await CheckSectionAsync(
            "Existing: 0",
            "Value: 1",
            static resource => new YamlSectionResource(resource, "Settings")
        );
    }

    private static async Task CheckSectionAsync(
        string document,
        string sectionContent,
        Func<InMemoryResource, IResourceReader> createSection
    )
    {
        var resource = new InMemoryResource();
        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes(document)));
        var section = createSection(resource);
        var writer = (IResourceWriter)section;
        await writer.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes(sectionContent),
                RevisionCondition.MustNotExist
            )
        );
        (await section.ReadAsync()).Status.ShouldBe(StateReadStatus.Success);
        await Should.ThrowAsync<StateConflictException>(async () =>
            await writer.WriteAsync(
                new ResourceWriteRequest(
                    Encoding.UTF8.GetBytes(sectionContent),
                    RevisionCondition.MustNotExist
                )
            )
        );
    }

    [Test]
    public async Task ZipAbsenceChecksEntryWhileUncheckedWritesIgnoreCachedSnapshots()
    {
        using var content = new MemoryStream();
        using (var zip = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("existing.txt");
        }
        var resource = new InMemoryResource();
        await resource.WriteAsync(new ResourceWriteRequest(content.ToArray()));
        var entry = new ZipEntryResource(resource, "new.txt");
        await entry.ReadAsync();
        await entry.WriteAsync(
            new ResourceWriteRequest("first"u8.ToArray(), RevisionCondition.MustNotExist)
        );
        await Should.ThrowAsync<StateConflictException>(async () =>
            await entry.WriteAsync(
                new ResourceWriteRequest("duplicate"u8.ToArray(), RevisionCondition.MustNotExist)
            )
        );
        await entry.WriteAsync(
            new ResourceWriteRequest("unchecked"u8.ToArray(), RevisionCondition.None)
        );
        Encoding.UTF8.GetString((await entry.ReadAsync()).Content.Span).ShouldBe("unchecked");
    }

    [Test]
    public async Task NullCodecValues_AreInvalidInsteadOfSuccessfulMissingValues()
    {
        var resource = new InMemoryResource();
        await resource.WriteAsync(new ResourceWriteRequest("null"u8.ToArray()));
        var source = new StateSource<string>("null-json", new SerializedSource<string>(resource, new JsonStateCodec<string>(), writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<string>());
        var result = await source.Reader.ReadAsync();
        result.Status.ShouldBe(StateReadStatus.InvalidPayload);
        result.Value.ShouldBeNull();
        result.Revision.ShouldNotBeNull();
    }

    [Test]
    public async Task ApplicationSaves_ReturnSourceReceiptsForSingleWritesAndEmptyReceiptsForNoOps()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>();
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new StateSource<AppSettings.Fragment>("user", store, new StateSourceOptions<AppSettings.Fragment> { Writer = store })])
        );
        IWritableState<AppSettings> writable = options;
        var written = await writable.SaveAsync(new AppSettings.Patch { RetryCount = 8 });
        written.Sources.Count.ShouldBe(1);
        written.Sources[0].SourceId.ShouldBe(SourceId.From("user"));
        written.Sources[0].ResourceId.ShouldBeNull();
        written.Revision.ShouldBe((await store.ReadAsync()).Revision);
        written.PhysicalWriteCount.ShouldBe(1);
        var empty = await writable.SaveAsync(new AppSettings.Patch());
        empty.Sources.ShouldBeEmpty();
        empty.PhysicalWriteCount.ShouldBe(0);
        empty.Revision.ShouldBeNull();
        var sourceEmpty = await options.ApplyPatchesAsync([
            new StateSourcePatch(SourceId.From("user"), new AppSettings.Patch()),
        ]);
        sourceEmpty.Sources.ShouldBeEmpty();
        sourceEmpty.PhysicalWriteCount.ShouldBe(0);
        sourceEmpty.Revision.ShouldBeNull();
    }
}
