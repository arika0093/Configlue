using System.Buffers;
using System.IO.Compression;
using Configlue.Provider.Json;
using Configlue.Provider.Zip;
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
        await archive.WriteAsync(new ResourceWriteRequest(CreateArchive(
            ("settings.json", Encode(codec, new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
            })),
            ("user.json", Encode(codec, new AppSettings.Fragment
            {
                Label = Optional<string?>.Present("before"),
            })),
            ("assets/keep.bin", untouchedBytes))));

        var settings = new ZipEntryResource(archive, "settings.json");
        var user = new ZipEntryResource(archive, "user.json");
        var sources = new StateSourceSet<AppSettings.Fragment>(
        [
            SerializedStateSource.FromResource<AppSettings.Fragment>("user", user, codec, priority: 100),
            SerializedStateSource.FromResource<AppSettings.Fragment>("settings", settings, codec, priority: 50),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sources);
        var plan = new StateWritePlan(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Label"] = "user",
            ["RetryCount"] = "settings",
        });

        using var session = await options.BeginConfigureAsync(plan);
        session.Value.Label = "after";
        session.Value.RetryCount = 12;
        var result = await session.SaveAsync();

        await Assert.That(archive.WriteCount).IsEqualTo(2);
        await Assert.That(result.MultiWriteResult).IsNotNull();
        await Assert.That(result.MultiWriteResult!.PhysicalWriteCount).IsEqualTo(1);
        var resolved = (await options.ReadAsync()).Value!;
        await Assert.That(resolved.Label).IsEqualTo("after");
        await Assert.That(resolved.RetryCount).IsEqualTo(12);

        var stored = await archive.ReadAsync();
        using var stream = new MemoryStream(stored.Content.ToArray(), writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        await Assert.That(zip.Entries.Select(static entry => entry.FullName))
            .IsEquivalentTo(["settings.json", "user.json", "assets/keep.bin"]);
        using var untouched = zip.GetEntry("assets/keep.bin")!.Open();
        using var copied = new MemoryStream();
        await untouched.CopyToAsync(copied);
        await Assert.That(copied.ToArray().SequenceEqual(untouchedBytes)).IsTrue();
    }

    [Test]
    public async Task WritingAnEntryCreatesAMissingArchive()
    {
        var archive = new InMemoryResource();
        var entry = new ZipEntryResource(archive, "new/settings.json");
        var before = await entry.ReadAsync();

        await Assert.That(before.Status).IsEqualTo(StateReadStatus.NotFound);
        await entry.WriteAsync(new ResourceWriteRequest(new byte[] { 6, 7 }, before.Revision, CheckRevision: true));

        var after = await entry.ReadAsync();
        await Assert.That(after.Status).IsEqualTo(StateReadStatus.Success);
        await Assert.That(after.Content.ToArray().SequenceEqual(new byte[] { 6, 7 })).IsTrue();
    }

    [Test]
    public async Task EntryViewsUseArchiveRevisionForConditionalWrites()
    {
        var archive = new InMemoryResource();
        await archive.WriteAsync(new ResourceWriteRequest(CreateArchive(("a.json", new byte[] { 1 }))));
        var firstEntry = new ZipEntryResource(archive, archive, "a.json");
        var secondEntry = new ZipEntryResource(archive, archive, "b.json");
        var firstRead = await firstEntry.ReadAsync();
        var secondRead = await secondEntry.ReadAsync();

        await Assert.That(firstRead.Revision).IsEqualTo(secondRead.Revision);
        await firstEntry.WriteAsync(new ResourceWriteRequest(new byte[] { 2 }, firstRead.Revision, CheckRevision: true));

        var staleWriteRejected = false;
        try
        {
            await secondEntry.WriteAsync(new ResourceWriteRequest(new byte[] { 3 }, secondRead.Revision, CheckRevision: true));
        }
        catch (StateConflictException)
        {
            staleWriteRejected = true;
        }

        await Assert.That(staleWriteRejected).IsTrue();
    }

    [Test]
    public async Task EntryPathsRejectRootedAndTraversingNames()
    {
        var archive = new InMemoryResource();
        foreach (var invalidName in new[] { "../outside", "folder/../outside", "/rooted", "C:\\rooted", "folder//entry", "folder/./entry" })
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

            await Assert.That(rejected).IsTrue();
        }
    }

    private static byte[] Encode(JsonStateCodec<AppSettings.Fragment> codec, AppSettings.Fragment fragment)
    {
        var destination = new ArrayBufferWriter<byte>();
        var context = default(StateCodecContext);
        codec.Serialize(fragment, destination, in context);
        return destination.WrittenMemory.ToArray();
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
}
