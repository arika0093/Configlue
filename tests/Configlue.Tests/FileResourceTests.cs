using System.Buffers;
using System.Text;
using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class FileResourceTests
{
    [Test]
    public async Task FileResource_UsesAtomicRevisionsBackupsAndConditionalWrites()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(path);

        try
        {
            var missing = await resource.ReadAsync();
            var firstContent = Encoding.UTF8.GetBytes("{\"value\":1}");
            var firstWrite = await resource.WriteAsync(
                new ResourceWriteRequest(firstContent, CheckRevision: true)
            );
            var current = await resource.ReadAsync();
            var missingRevisionConflict = false;
            try
            {
                await resource.WriteAsync(
                    new ResourceWriteRequest(Encoding.UTF8.GetBytes("ignored"), CheckRevision: true)
                );
            }
            catch (StateConflictException)
            {
                missingRevisionConflict = true;
            }

            var conflict = false;
            try
            {
                await resource.WriteAsync(
                    new ResourceWriteRequest(Encoding.UTF8.GetBytes("{}"), "stale-revision")
                );
            }
            catch (StateConflictException)
            {
                conflict = true;
            }

            var secondContent = Encoding.UTF8.GetBytes("{\"value\":2}");
            var secondWrite = await resource.WriteAsync(
                new ResourceWriteRequest(secondContent, firstWrite.Revision)
            );
            var backup = await File.ReadAllBytesAsync(GetDefaultBackupPath(path));
            var saved = await resource.ReadAsync();

            (missing.Status).ShouldBe(StateReadStatus.NotFound);
            (current.Status).ShouldBe(StateReadStatus.Success);
            (current.Revision).ShouldBe(firstWrite.Revision);
            (missingRevisionConflict).ShouldBeTrue();
            (conflict).ShouldBeTrue();
            (secondWrite.Revision).ShouldNotBe(firstWrite.Revision);
            (Encoding.UTF8.GetString(backup)).ShouldBe("{\"value\":1}");
            (Encoding.UTF8.GetString(saved.Content.Span)).ShouldBe("{\"value\":2}");
        }
        finally
        {
            resource.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public async Task FileResource_WritesLargePayloadWithoutChangingItsBytesOrRevision()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );
        var path = System.IO.Path.Combine(directory, "large-settings.json");
        using var resource = new FileResource(
            path,
            new FileResourceOptions { CreateBackup = false, BackupMaxCount = 0 }
        );
        var content = new byte[1024 * 1024];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(index % 251);
        }

        try
        {
            var write = await resource.WriteAsync(
                new ResourceWriteRequest(content, CheckRevision: true)
            );
            var saved = await resource.ReadAsync();

            saved.Status.ShouldBe(StateReadStatus.Success);
            saved.Revision.ShouldBe(write.Revision);
            saved.Content.Span.SequenceEqual(content).ShouldBeTrue();
        }
        finally
        {
            resource.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public async Task FileResource_RotatesBackupsAndRestoresTheLatestFromConfiguredDirectory()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );
        var backupDirectory = System.IO.Path.Combine(directory, "backups");
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(
            path,
            new FileResourceOptions { BackupDirectory = backupDirectory, BackupMaxCount = 2 }
        );

        try
        {
            for (var value = 0; value < 4; value++)
            {
                await resource.WriteAsync(
                    new ResourceWriteRequest(Encoding.UTF8.GetBytes($"{{\"value\":{value}}}"))
                );
            }

            var latestBackup = await File.ReadAllTextAsync(
                System.IO.Path.Combine(backupDirectory, "settings.json.bak")
            );
            var olderBackup = await File.ReadAllTextAsync(
                System.IO.Path.Combine(backupDirectory, "settings.json.bak.1")
            );
            var restored = await resource.RestoreLatestBackupAsync();
            var current = await resource.ReadAsync();

            (latestBackup).ShouldBe("{\"value\":2}");
            (olderBackup).ShouldBe("{\"value\":1}");
            (restored.Revision).ShouldBe(current.Revision);
            (Encoding.UTF8.GetString(current.Content.Span)).ShouldBe("{\"value\":2}");
        }
        finally
        {
            resource.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public async Task SerializedReader_RecoversMissingFileBeforeSelectingFallbackSource()
    {
        var directory = CreateTemporaryDirectory();
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(
            path,
            new FileResourceOptions { AutomaticBackupRecovery = true }
        );
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var backupContent = SerializeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) },
            codec
        );
        await resource.WriteAsync(new ResourceWriteRequest(backupContent));
        await resource.WriteAsync(
            new ResourceWriteRequest(
                SerializeFragment(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) },
                    codec
                )
            )
        );
        File.Delete(path);

        var primarySource = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "primary-file",
            resource,
            codec,
            priority: 100
        );
        var fallbackStore = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(99) }
        );
        var fallbackSource = new StateSource<AppSettings.Fragment>("fallback", fallbackStore);
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([primarySource, fallbackSource])
        );

        var recovered = await options.ReadAsync();

        recovered.Status.ShouldBe(StateReadStatus.Success);
        recovered.Value!.RetryCount.ShouldBe(4);
        recovered.SourceId.ShouldBe("primary-file");
        (await File.ReadAllTextAsync(path)).ShouldBe(Encoding.UTF8.GetString(backupContent));
        recovered.Revision.ShouldBe((await resource.ReadAsync()).Revision);
    }

    [Test]
    public async Task SerializedReader_LeavesRecoveryDisabledByDefault()
    {
        var directory = CreateTemporaryDirectory();
        using var cleanup = new DirectoryCleanup(directory);
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(path);
        await File.WriteAllTextAsync(GetLegacyBackupPath(path), "{}");
        var reader = new SerializedStateReader<AppSettings.Fragment>(
            resource,
            new JsonStateCodec<AppSettings.Fragment>()
        );

        var result = await reader.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.NotFound);
        File.Exists(path).ShouldBeFalse();
    }

    [Test]
    public async Task SerializedReader_RecoversCorruptPrimaryOnlyAfterBackupValidation()
    {
        var directory = CreateTemporaryDirectory();
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(
            path,
            new FileResourceOptions { AutomaticBackupRecovery = true }
        );
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var backupContent = SerializeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) },
            codec
        );
        await resource.WriteAsync(new ResourceWriteRequest(backupContent));
        await resource.WriteAsync(
            new ResourceWriteRequest(
                SerializeFragment(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) },
                    codec
                )
            )
        );
        await File.WriteAllTextAsync(path, "{ invalid json");
        var reader = new SerializedStateReader<AppSettings.Fragment>(resource, codec);

        var recovered = await reader.ReadAsync();

        recovered.Status.ShouldBe(StateReadStatus.Success);
        recovered.Value!.RetryCount.ShouldBe(4);
        (await File.ReadAllTextAsync(path)).ShouldBe(Encoding.UTF8.GetString(backupContent));
        recovered.Revision.ShouldBe((await resource.ReadAsync()).Revision);
    }

    [Test]
    public async Task SerializedReader_LeavesPrimaryUntouchedWhenBackupIsInvalid()
    {
        var directory = CreateTemporaryDirectory();
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(
            path,
            new FileResourceOptions { AutomaticBackupRecovery = true }
        );
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "{ invalid primary");
        var corruptPrimary = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(GetLegacyBackupPath(path), "{ invalid backup");
        var reader = new SerializedStateReader<AppSettings.Fragment>(
            resource,
            new JsonStateCodec<AppSettings.Fragment>()
        );

        await Should.ThrowAsync<JsonException>(async () => await reader.ReadAsync());

        (await File.ReadAllTextAsync(path)).ShouldBe(corruptPrimary);
    }

    [Test]
    public async Task FileResource_RejectsAutomaticRecoveryAfterObservedRevisionChanges()
    {
        var directory = CreateTemporaryDirectory();
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(path);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "current");
        await File.WriteAllTextAsync(GetLegacyBackupPath(path), "backup");

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.TryRecoverLatestBackupAsync(
                "stale-revision",
                expectedMissing: false,
                static (_, _) => ValueTask.FromResult(true),
                CancellationToken.None
            )
        );

        (await File.ReadAllTextAsync(path)).ShouldBe("current");
    }

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
                    CheckRevision: true
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
                            "stale-revision"
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
                new ResourceWriteRequest(Encoding.UTF8.GetBytes(content), revision)
            );
            return false;
        }
        catch (StateConflictException)
        {
            return true;
        }
    }

    private static string CreateTemporaryDirectory() =>
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );

    private static string GetDefaultBackupPath(string path)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path)!,
            OperatingSystem.IsWindows() ? "backup" : ".backup"
        );
        Directory.CreateDirectory(directory);
        return System.IO.Path.Combine(directory, System.IO.Path.GetFileName(path) + ".bak");
    }

    private static string GetLegacyBackupPath(string path)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path)!,
            OperatingSystem.IsWindows() ? "backup" : ".backup"
        );
        Directory.CreateDirectory(directory);
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (!OperatingSystem.IsWindows())
        {
            name = "." + name;
        }

        name +=
            "_" + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return System.IO.Path.Combine(directory, name + System.IO.Path.GetExtension(path) + ".bak");
    }

    private static byte[] SerializeFragment(
        AppSettings.Fragment fragment,
        JsonStateCodec<AppSettings.Fragment> codec
    )
    {
        var output = new ArrayBufferWriter<byte>();
        codec.Serialize(fragment, output, default);
        return output.WrittenSpan.ToArray();
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class DirectoryCleanup(string directory) : IDisposable
    {
        public void Dispose() => DeleteDirectory(directory);
    }
}
