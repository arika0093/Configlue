using System.Buffers;
using System.Text;
using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.Testing;

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
    public async Task FileResource_KeepsTheSidecarLockFilePersistent()
    {
        var directory = CreateTemporaryDirectory();
        var path = System.IO.Path.Combine(directory, "settings.json");
        var lockPath = FileResource.ResolveLockPathForTests(path);
        try
        {
            using var resource = new FileResource(path);
            lockPath = resource.LockPathForTests;
            await resource.WriteAsync(
                new ResourceWriteRequest(Encoding.UTF8.GetBytes("{\"value\":1}"))
            );
            File.Exists(lockPath).ShouldBeTrue();
            await resource.WriteAsync(
                new ResourceWriteRequest(Encoding.UTF8.GetBytes("{\"value\":2}"))
            );
            File.Exists(lockPath).ShouldBeTrue();
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteFileIfExists(lockPath);
        }
    }

    [Test]
    public void FileResource_PlacesTheSidecarLockOutsideTheResourceDirectoryByDefault()
    {
        var directory = CreateTemporaryDirectory();
        var path = System.IO.Path.Combine(directory, "settings.json");
        try
        {
            using var resource = new FileResource(path);
            var lockPath = resource.LockPathForTests;
            lockPath.ShouldStartWith(
                ConfiglueStandardPaths.GetSharedLockDirectory()
                    + System.IO.Path.DirectorySeparatorChar
            );
            System.IO.Path.GetDirectoryName(lockPath).ShouldNotBe(directory);
            File.Exists(System.IO.Path.Combine(directory, ".settings.json.configlue.lock"))
                .ShouldBeFalse();
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteFileIfExists(FileResource.ResolveLockPathForTests(path));
        }
    }

    [Test]
    public void FileResource_SupportsLegacyCoLocatedLocksWithSlashDirectory()
    {
        var directory = CreateTemporaryDirectory();
        var path = System.IO.Path.Combine(directory, "settings.json");
        try
        {
            using var resource = new FileResource(
                path,
                new FileResourceOptions { LockDirectory = "/" }
            );
            resource.LockPathForTests.ShouldBe(
                System.IO.Path.Combine(directory, ".settings.json.configlue.lock")
            );
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Test]
    public void FileResource_HonorsCustomLockDirectories()
    {
        var directory = CreateTemporaryDirectory();
        var customDirectory = CreateTemporaryDirectory();
        var path = System.IO.Path.Combine(directory, "settings.json");
        try
        {
            using var resource = new FileResource(
                path,
                new FileResourceOptions { LockDirectory = customDirectory }
            );
            resource.LockPathForTests.ShouldStartWith(
                System.IO.Path.GetFullPath(customDirectory) + System.IO.Path.DirectorySeparatorChar
            );
            FileResource
                .ResolveLockPathForTests(path, customDirectory)
                .ShouldBe(resource.LockPathForTests);
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteDirectory(customDirectory);
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
        var targetPath = System.IO.Path.Combine(directory, "b.json");
        var otherLockPath = FileResource.ResolveLockPathForTests(
            System.IO.Path.Combine(directory, "a.json")
        );
        try
        {
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(otherLockPath)!);
            using (
                var _ = new FileStream(
                    otherLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                )
            )
            {
                using var resource = new FileResource(
                    targetPath,
                    new FileResourceOptions { LockAcquireTimeout = TimeSpan.FromSeconds(10) }
                );
                await resource.WriteAsync(
                    new ResourceWriteRequest(Encoding.UTF8.GetBytes("{\"value\":2}"))
                );
            }

            File.Exists(targetPath).ShouldBeTrue();
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteFileIfExists(otherLockPath);
            DeleteFileIfExists(FileResource.ResolveLockPathForTests(targetPath));
        }
    }

    [Test]
    public async Task FileResource_HonorsCancellationWhileWaitingForTheLock()
    {
        var directory = CreateTemporaryDirectory();
        var path = System.IO.Path.Combine(directory, "settings.json");
        var lockPath = FileResource.ResolveLockPathForTests(path);
        try
        {
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(lockPath)!);
            using (
                var _ = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                )
            )
            {
                using var resource = new FileResource(
                    path,
                    new FileResourceOptions { LockAcquireTimeout = TimeSpan.FromSeconds(30) }
                );
                using var cancellation = new CancellationTokenSource();
                var write = resource
                    .WriteAsync(
                        new ResourceWriteRequest(Encoding.UTF8.GetBytes("{}")),
                        cancellation.Token
                    )
                    .AsTask();
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                cancellation.Cancel();

                await Should.ThrowAsync<OperationCanceledException>(async () => await write);
            }
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteFileIfExists(lockPath);
        }
    }

    [Test]
    public async Task FileResource_FailsWhenTheLockAcquireTimeoutExpires()
    {
        var directory = CreateTemporaryDirectory();
        var path = System.IO.Path.Combine(directory, "settings.json");
        var lockPath = FileResource.ResolveLockPathForTests(path);
        try
        {
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(lockPath)!);
            using (
                var _ = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                )
            )
            {
                using var resource = new FileResource(
                    path,
                    new FileResourceOptions
                    {
                        LockAcquireTimeout = TimeSpan.FromMilliseconds(150),
                        LockAcquireRetryDelay = TimeSpan.FromMilliseconds(10),
                    }
                );

                await Should.ThrowAsync<IOException>(async () =>
                    await resource.WriteAsync(
                        new ResourceWriteRequest(Encoding.UTF8.GetBytes("{}"))
                    )
                );
            }
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteFileIfExists(lockPath);
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
