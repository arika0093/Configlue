using System.Buffers;
using System.IO.Compression;
using Configlue.Provider.Json;
using Configlue.Resource.Zip;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class ZipEntryResourceTests
{
    [Test]
    public async Task RoutedSourceEditsSharingAnArchiveWriteOnceAndPreserveOtherEntries()
    {
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var archive = new InMemoryResource();
        var untouchedBytes = new byte[] { 1, 4, 9, 16 };
        await archive.WriteAsync(
            new ResourceWriteRequest(
                CreateArchive(
                    (
                        "settings.json",
                        Encode(
                            codec,
                            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
                        )
                    ),
                    (
                        "user.json",
                        Encode(
                            codec,
                            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
                        )
                    ),
                    ("assets/keep.bin", untouchedBytes)
                )
            )
        );

        var settings = new ZipEntryResource(archive, "settings.json");
        var user = new ZipEntryResource(archive, "user.json");
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new StateSource<AppSettings.Fragment>("user", new SerializedSource<AppSettings.Fragment>(user, codec, writer: (IResourceReader)user as IResourceWriter, watcher: (IResourceReader)user as ISourceWatcher), new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
            new StateSource<AppSettings.Fragment>("settings", new SerializedSource<AppSettings.Fragment>(settings, codec, writer: (IResourceReader)settings as IResourceWriter, watcher: (IResourceReader)settings as ISourceWatcher), new StateSourceOptions<AppSettings.Fragment> { Priority = 50 }),
        ]);
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            sources,
            StateWritePlan.DefaultTo(SourceId.From("user"))
        );
        var plan = new StateWritePlan(
            null,
            new Dictionary<string, SourceId>(StringComparer.Ordinal)
            {
                ["Label"] = SourceId.From("user"),
                ["RetryCount"] = SourceId.From("settings"),
            }
        );

        using var session = await options.OpenEditSessionAsync(plan);
        session.Value.Label = "after";
        session.Value.RetryCount = 12;
        var result = await session.CommitAsync();

        (archive.WriteCount).ShouldBe(2);
        (result).ShouldNotBeNull();
        (result.PhysicalWriteCount).ShouldBe(1);
        var resolved = (await options.ReadAsync()).Value!;
        (resolved.Label).ShouldBe("after");
        (resolved.RetryCount).ShouldBe(12);

        var stored = await archive.ReadAsync();
        using var stream = new MemoryStream(stored.Content.ToArray(), writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        ((zip.Entries.Select(static entry => entry.FullName)))
            .OrderBy(static item => item)
            .ShouldBe(
                (new[] { "settings.json", "user.json", "assets/keep.bin" }).OrderBy(static item =>
                    item
                )
            );
        using var untouched = zip.GetEntry("assets/keep.bin")!.Open();
        using var copied = new MemoryStream();
        await untouched.CopyToAsync(copied);
        (copied.ToArray().SequenceEqual(untouchedBytes)).ShouldBeTrue();
    }

    [Test]
    public async Task WritingAnEntryCreatesAMissingArchive()
    {
        var archive = new InMemoryResource();
        var entry = new ZipEntryResource(archive, "new/settings.json");
        var before = await entry.ReadAsync();

        (before.Status).ShouldBe(StateReadStatus.NotFound);
        await entry.WriteAsync(
            new ResourceWriteRequest(
                new byte[] { 6, 7 },
                Condition: RevisionCondition.FromRevision(before.Revision)
            )
        );

        var after = await entry.ReadAsync();
        (after.Status).ShouldBe(StateReadStatus.Success);
        (after.Content.ToArray().SequenceEqual(new byte[] { 6, 7 })).ShouldBeTrue();
    }

    [Test]
    public async Task EntryViewsMergeDisjointWritesButRejectStaleWritesToSameEntry()
    {
        var archive = new InMemoryResource();
        await archive.WriteAsync(
            new ResourceWriteRequest(
                CreateArchive(("a.json", new byte[] { 1 }), ("b.json", new byte[] { 2 }))
            )
        );
        var firstEntry = new ZipEntryResource(archive, archive, "a.json");
        var secondEntry = new ZipEntryResource(archive, archive, "b.json");
        var firstRead = await firstEntry.ReadAsync();
        var secondRead = await secondEntry.ReadAsync();

        (firstRead.Revision).ShouldBe(secondRead.Revision);
        await firstEntry.WriteAsync(
            new ResourceWriteRequest(
                new byte[] { 2 },
                Condition: RevisionCondition.FromRevision(firstRead.Revision)
            )
        );
        await secondEntry.WriteAsync(
            new ResourceWriteRequest(
                new byte[] { 3 },
                Condition: RevisionCondition.FromRevision(secondRead.Revision)
            )
        );

        (await firstEntry.ReadAsync()).Content.ToArray().ShouldBe([2]);
        (await secondEntry.ReadAsync()).Content.ToArray().ShouldBe([3]);

        var conflictingEntry = new ZipEntryResource(archive, archive, "a.json");
        var staleRead = await firstEntry.ReadAsync();
        var conflictingRead = await conflictingEntry.ReadAsync();
        await firstEntry.WriteAsync(
            new ResourceWriteRequest(
                new byte[] { 4 },
                Condition: RevisionCondition.FromRevision(staleRead.Revision)
            )
        );
        await Should.ThrowAsync<StateConflictException>(async () =>
            await conflictingEntry.WriteAsync(
                new ResourceWriteRequest(
                    new byte[] { 6 },
                    Condition: RevisionCondition.FromRevision(conflictingRead.Revision)
                )
            )
        );
    }

    [Test]
    public async Task SubjectAwareEntryNamesShareOneArchiveAndKeepDisjointWritesComposable()
    {
        var archive = new InMemoryResource();
        var firstSubject = new ResourceSubject("one");
        var secondSubject = new ResourceSubject("two");
        var firstContext = new ConfiglueResourceContext(
            firstSubject,
            ResourceKey.From(firstSubject.Key),
            RouteKey.Default
        );
        var secondContext = new ConfiglueResourceContext(
            secondSubject,
            ResourceKey.From(secondSubject.Key),
            RouteKey.Default
        );
        var firstEntryName = $"settings/{firstSubject.Key.Value}.json";
        var secondEntryName = $"settings/{secondSubject.Key.Value}.json";
        await archive.WriteAsync(
            new ResourceWriteRequest(CreateArchive((firstEntryName, [1]), (secondEntryName, [2])))
        );

        var options = new ZipEntryResourceOptions
        {
            EntryNameSelector = context => $"settings/{context.ResourceKey.Value}.json",
        };
        var entry = new ZipEntryResource(archive, archive, options, "settings/default.json");
        var firstRead = await entry.ReadAsync(firstContext);
        var secondRead = await entry.ReadAsync(secondContext);

        firstRead.Content.ToArray().ShouldBe([1]);
        secondRead.Content.ToArray().ShouldBe([2]);
        entry.GetResourceId(firstContext).ShouldBe(entry.GetResourceId(secondContext));

        await entry.BatchWriter!.WriteBatchAsync([
            entry.CreateMutation(
                firstContext,
                new ResourceWriteRequest(
                    new byte[] { 3 },
                    Condition: RevisionCondition.FromRevision(firstRead.Revision)
                )
            ),
            entry.CreateMutation(
                secondContext,
                new ResourceWriteRequest(
                    new byte[] { 4 },
                    Condition: RevisionCondition.FromRevision(secondRead.Revision)
                )
            ),
        ]);

        (await entry.ReadAsync(firstContext)).Content.ToArray().ShouldBe([3]);
        (await entry.ReadAsync(secondContext)).Content.ToArray().ShouldBe([4]);
        archive.WriteCount.ShouldBe(2);
    }

    [Test]
    public async Task EntryPathsRejectRootedAndTraversingNames()
    {
        var archive = new InMemoryResource();
        foreach (
            var invalidName in new[]
            {
                "../outside",
                "folder/../outside",
                "/rooted",
                "C:\\rooted",
                "folder//entry",
                "folder/./entry",
            }
        )
        {
            var rejected = false;
            try
            {
                _ = new ZipEntryResource(archive, archive, invalidName);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            (rejected).ShouldBeTrue();
        }
    }

    [Test]
    public async Task EntryWatcherUsesConfigurablePollingWhenArchiveHasNoWatcher()
    {
        var archive = new RevisionReader();
        var entry = new ZipEntryResource(
            archive,
            "settings.json",
            pollingInterval: TimeSpan.FromMilliseconds(10)
        );
        var waiting = entry.WaitForChangeAsync("first").AsTask();
        await Task.Delay(35);
        archive.Revision = "second";

        await waiting.WaitAsync(TimeSpan.FromSeconds(2));

        (archive.ReadCount > 1).ShouldBeTrue();
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new ZipEntryResource(archive, "settings.json", pollingInterval: TimeSpan.Zero)
        );
    }

    [Test]
    public async Task CorruptArchiveReadPreservesInvalidPayload()
    {
        var archive = new CorruptArchive();
        var entry = new ZipEntryResource(archive, archive, "settings.json");

        var result = await entry.ReadAsync();

        (result.Status).ShouldBe(StateReadStatus.InvalidPayload);
        (result.Revision).ShouldBe("r1");
    }

    [Test]
    public async Task CorruptArchiveWritesDoNotReplaceArchiveWithNewZip()
    {
        var archive = new CorruptArchive();
        var entry = new ZipEntryResource(archive, archive, "settings.json");
        var observed = await entry.ReadAsync();
        (observed.Status).ShouldBe(StateReadStatus.InvalidPayload);

        await Should.ThrowAsync<InvalidDataException>(async () =>
            await entry.WriteAsync(new ResourceWriteRequest(new byte[] { 1 }))
        );
        await Should.ThrowAsync<InvalidDataException>(async () =>
            await entry.WriteAsync(
                new ResourceWriteRequest(
                    new byte[] { 2 },
                    Condition: RevisionCondition.MustNotExist
                )
            )
        );
        await Should.ThrowAsync<InvalidDataException>(async () =>
            await entry.WriteAsync(
                new ResourceWriteRequest(
                    new byte[] { 3 },
                    Condition: RevisionCondition.FromRevision(observed.Revision)
                )
            )
        );

        (archive.WriteCount).ShouldBe(0);
        var current = await archive.ReadAsync();
        (current.Status).ShouldBe(StateReadStatus.InvalidPayload);
        (current.Revision).ShouldBe("r1");
    }

    private static byte[] Encode(
        JsonStateCodec<AppSettings.Fragment> codec,
        AppSettings.Fragment fragment
    )
    {
        var destination = new ArrayBufferWriter<byte>();
        var context = default(StateCodecContext);
        codec.Serialize(fragment, destination, in context);
        return destination.WrittenMemory.ToArray();
    }

    private sealed class RevisionReader : IResourceReader
    {
        public string Revision { get; set; } = "first";

        public int ReadCount { get; private set; }

        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTaskCompat.FromResult(
                ResourceReadResult.Success(ReadOnlyMemory<byte>.Empty, Revision)
            );
        }
    }

    private sealed class CorruptArchive : IResourceReader, IResourceBatchWriter
    {
        public int WriteCount { get; private set; }

        public ResourceId GetResourceId(ConfiglueResourceContext context) =>
            new ResourceId("corrupt-archive");

        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(ResourceReadResult.InvalidPayload("r1"));
        }

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            ResourceWriteRequest request,
            CancellationToken cancellationToken = default
        ) => WriteBatchAsync([ResourceWriteMutation.Replace(request, context)], cancellationToken);

        public ValueTask<StateWriteResult> WriteBatchAsync(
            IReadOnlyList<ResourceWriteMutation> mutations,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResourceWriteMutation.ValidateBatch(mutations);
            var current = ResourceReadResult.InvalidPayload("r1");
            foreach (var mutation in mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = mutation.Apply(current).ToArray();
                current = ResourceReadResult.Success(content, "r2");
            }

            WriteCount++;
            return ValueTaskCompat.FromResult(new StateWriteResult(current.Revision));
        }
    }

    private static byte[] CreateArchive(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(content);
            }
        }

        return stream.ToArray();
    }

    private sealed record ResourceSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }
}
