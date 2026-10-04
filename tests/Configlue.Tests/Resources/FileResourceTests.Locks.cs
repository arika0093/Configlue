using System.Text;

namespace Configlue.Tests;

public sealed partial class FileResourceTests
{
    [Test]
    [NotInParallel]
    public async Task FileResource_RemovesProcessLockEntriesAfterOperationsFinish()
    {
        var directory = CreateTemporaryDirectory();
        var baseline = FileResource.ProcessLockCount;
        try
        {
            for (var index = 0; index < 64; index++)
            {
                var path = System.IO.Path.Combine(directory, $"settings-{index}.json");
                using var resource = new FileResource(path);
                await resource.WriteAsync(
                    new ResourceWriteRequest(Encoding.UTF8.GetBytes("{\"value\":1}"))
                );
                FileResource.HasProcessLockFor(System.IO.Path.GetFullPath(path)).ShouldBeFalse();
            }

            FileResource.ProcessLockCount.ShouldBe(baseline);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Test]
    public async Task FileResource_SerializesConditionalWritesAcrossInstancesForTheSameNormalizedPath()
    {
        var directory = CreateTemporaryDirectory();
        var nested = System.IO.Path.Combine(directory, "nested");
        var path = System.IO.Path.Combine(directory, "settings.json");
        var alternate = System.IO.Path.Combine(nested, "..", "settings.json");
        try
        {
            Directory.CreateDirectory(nested);
            using var first = new FileResource(path);
            using var second = new FileResource(alternate);
            second.Path.ShouldBe(first.Path);

            var initial = await first.WriteAsync(
                new ResourceWriteRequest(
                    Encoding.UTF8.GetBytes("{\"value\":0}"),
                    Condition: RevisionCondition.MustNotExist
                )
            );

            var conflicts = await Task.WhenAll(
                Task.Run(() => WriteWithRevisionAsync(first, "{\"value\":1}", initial.Revision)),
                Task.Run(() => WriteWithRevisionAsync(second, "{\"value\":2}", initial.Revision))
            );

            conflicts.Count(static conflict => conflict).ShouldBe(1);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Test]
    public async Task FileResource_WritesToDifferentPathsDoNotBlockEachOther()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            using var first = new FileResource(System.IO.Path.Combine(directory, "a.json"));
            using var second = new FileResource(System.IO.Path.Combine(directory, "b.json"));

            await Task.WhenAll(
                first.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("{\"value\":1}"))).AsTask(),
                second.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("{\"value\":2}"))).AsTask()
            );

            File.Exists(System.IO.Path.Combine(directory, "a.json")).ShouldBeTrue();
            File.Exists(System.IO.Path.Combine(directory, "b.json")).ShouldBeTrue();
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Test]
    public async Task FileResource_ReleasesTheProcessLockEntryAfterAFailedWrite()
    {
        var directory = CreateTemporaryDirectory();
        var path = System.IO.Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            using (var seed = new FileResource(path))
            {
                await seed.WriteAsync(
                    new ResourceWriteRequest(Encoding.UTF8.GetBytes("{\"value\":1}"))
                );
            }

            using (var resource = new FileResource(path))
            {
                await Should.ThrowAsync<StateConflictException>(async () =>
                    await resource.WriteAsync(
                        new ResourceWriteRequest(
                            Encoding.UTF8.GetBytes("{\"value\":2}"),
                            Condition: RevisionCondition.FromRevision("stale-revision")
                        )
                    )
                );
            }

            FileResource.HasProcessLockFor(System.IO.Path.GetFullPath(path)).ShouldBeFalse();
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static async Task<bool> WriteWithRevisionAsync(
        FileResource resource,
        string content,
        string? revision
    )
    {
        try
        {
            await resource.WriteAsync(
                new ResourceWriteRequest(
                    Encoding.UTF8.GetBytes(content),
                    Condition: RevisionCondition.FromRevision(revision)
                )
            );
            return false;
        }
        catch (StateConflictException)
        {
            return true;
        }
    }
}
