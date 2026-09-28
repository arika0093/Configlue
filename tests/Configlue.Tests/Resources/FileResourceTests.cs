using System.Buffers;
using System.Text;
using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed partial class FileResourceTests
{
    [Test]
    public async Task FileResource_PipelineReadMatchesMemoryRead()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "pipeline-content.bin");
        var content = Encoding.UTF8.GetBytes(new string('x', 256 * 1024));
        await File.WriteAllBytesAsync(path, content);
        using var resource = new FileResource(path);

        var expected = await resource.ReadAsync();
        await using var pipelineResult = await resource.ReadPipelineAsync();
        var actual = await pipelineResult.ReadAllAsync();
        var copied = new byte[content.Length];
        actual.CopyTo(copied);
        pipelineResult.Content!.AdvanceTo(actual.End);

        pipelineResult.Status.ShouldBe(expected.Status);
        pipelineResult.Revision.ShouldBe(expected.Revision);
        copied.ShouldBe(content);
    }

    [Test]
    public async Task SerializedStateReader_PrefersPipelineResourceReads()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, """{"$version":2,"RetryCount":42}""");
        using var resource = new FileResource(path);
        var reader = new SerializedStateReader<AppSettings.Fragment>(
            resource,
            new JsonStateCodec<AppSettings.Fragment> { UseAsyncStreamDecoding = true }
        );

        var result = await reader.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Value!.RetryCount.Value.ShouldBe(42);
        result.Revision.ShouldNotBeNull();
        result.Revision.ShouldBe((await resource.ReadAsync()).Revision);
    }

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
                new ResourceWriteRequest(firstContent, Condition: RevisionCondition.MustNotExist)
            );
            var current = await resource.ReadAsync();
            var missingRevisionConflict = false;
            try
            {
                await resource.WriteAsync(
                    new ResourceWriteRequest(
                        Encoding.UTF8.GetBytes("ignored"),
                        Condition: RevisionCondition.MustNotExist
                    )
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
                    new ResourceWriteRequest(
                        Encoding.UTF8.GetBytes("{}"),
                        Condition: RevisionCondition.FromRevision("stale-revision")
                    )
                );
            }
            catch (StateConflictException)
            {
                conflict = true;
            }

            var secondContent = Encoding.UTF8.GetBytes("{\"value\":2}");
            var secondWrite = await resource.WriteAsync(
                new ResourceWriteRequest(
                    secondContent,
                    Condition: RevisionCondition.FromRevision(firstWrite.Revision)
                )
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
                new ResourceWriteRequest(content, Condition: RevisionCondition.MustNotExist)
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
    public async Task FileResource_WriteAsyncSnapshotsCallerMemoryBeforeAwaiting()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );
        var path = System.IO.Path.Combine(directory, "settings.bin");
        using var resource = new FileResource(
            path,
            new FileResourceOptions { CreateBackup = false, BackupMaxCount = 0 }
        );
        var content = new byte[] { 1, 2, 3 };

        try
        {
            var write = resource.WriteAsync(new ResourceWriteRequest(content));
            content[0] = 9;
            await write;

            var saved = await resource.ReadAsync();

            saved.Content.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
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
    public async Task FileResource_StoresVersionedBackupsUnderTheConfiguredPersistentRoot()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var backupRoot = Path.Combine(directory, "persistent");
        var resourcePath = Path.Combine(directory, "settings", "settings.json");
        var backupDirectory = Path.Combine(backupRoot, "application-backups", "settings-model.v3");
        var resourceOptions = new FileResourceOptions
        {
            BackupRootDirectory = backupRoot,
            BackupDirectoryName = "application-backups",
            BackupMaxCount = 2,
        };
        var backupSchema = new StateSchemaMetadata("settings-model", 3);
        using var resource = new FileResource(resourcePath, backupSchema, resourceOptions);

        for (var value = 0; value < 4; value++)
        {
            await resource.WriteAsync(
                new ResourceWriteRequest(Encoding.UTF8.GetBytes($"{{\"value\":{value}}}"))
            );
        }

        var latestBackup = Directory.GetFiles(backupDirectory, "settings.json.*.bak").Single();
        var olderBackup = latestBackup + ".1";
        (await File.ReadAllTextAsync(latestBackup)).ShouldBe("{\"value\":2}");
        (await File.ReadAllTextAsync(olderBackup)).ShouldBe("{\"value\":1}");

        using var otherResource = new FileResource(
            Path.Combine(directory, "other", "settings.json"),
            backupSchema,
            resourceOptions
        );
        await otherResource.WriteAsync(new ResourceWriteRequest("other first"u8.ToArray()));
        await otherResource.WriteAsync(new ResourceWriteRequest("other second"u8.ToArray()));
        await otherResource.WriteAsync(new ResourceWriteRequest("other third"u8.ToArray()));

        var backupContents = await Task.WhenAll(
            Directory
                .GetFiles(backupDirectory)
                .Select(static backupPath => File.ReadAllTextAsync(backupPath))
        );
        backupContents.Length.ShouldBe(4);
        backupContents.ShouldContain("other first");
        backupContents.ShouldContain("other second");
    }

    [Test]
    public async Task FileResource_PersistentBackupOptionsSupportFlatAndResourceDirectoryLayouts()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var backupRoot = Path.Combine(directory, "persistent");
        var flatPath = Path.Combine(directory, "flat.json");
        using (
            var flatResource = new FileResource(
                flatPath,
                new FileResourceOptions
                {
                    BackupDirectoryMode = FileBackupDirectoryMode.PersistentUserDirectory,
                    BackupRootDirectory = "persistent",
                    BackupDirectoryName = "flat-backups",
                    IncludeModelVersionInBackupDirectory = false,
                }
            )
        )
        {
            await flatResource.WriteAsync(new ResourceWriteRequest("first"u8.ToArray()));
            await flatResource.WriteAsync(new ResourceWriteRequest("second"u8.ToArray()));
        }

        Directory
            .GetFiles(Path.Combine(backupRoot, "flat-backups"), "flat.json.*.bak")
            .Length.ShouldBe(1);

        var localPath = Path.Combine(directory, "local.json");
        using (
            var localResource = new FileResource(
                localPath,
                new FileResourceOptions
                {
                    BackupDirectoryMode = FileBackupDirectoryMode.ResourceDirectory,
                }
            )
        )
        {
            await localResource.WriteAsync(new ResourceWriteRequest("first"u8.ToArray()));
            await localResource.WriteAsync(new ResourceWriteRequest("second"u8.ToArray()));
        }

        File.Exists(GetDefaultBackupPath(localPath)).ShouldBeTrue();
    }

    [Test]
    public void FileResource_RejectsBackupDirectoryNamesThatEscapeTheBackupRoot()
    {
        var directory = CreateTemporaryDirectory();
        Should.Throw<ArgumentException>(() =>
            new FileResource(
                Path.Combine(directory, "settings.json"),
                new FileResourceOptions
                {
                    BackupRootDirectory = directory,
                    BackupDirectoryName = "../outside",
                    IncludeModelVersionInBackupDirectory = false,
                }
            )
        );
    }

    [Test]
    public async Task FileResource_PersistentBackupsCanRecoverThePreviousResourceDirectoryLayout()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var resourcePath = Path.Combine(directory, "settings.json");
        var previousBackupDirectory = Path.Combine(
            directory,
            OperatingSystem.IsWindows() ? "backup" : ".backup"
        );
        Directory.CreateDirectory(previousBackupDirectory);
        var previousBackupPath = Path.Combine(previousBackupDirectory, "settings.json.bak");
        await File.WriteAllTextAsync(previousBackupPath, "recoverable");
        using var resource = new FileResource(
            resourcePath,
            new StateSchemaMetadata("settings-model", 3),
            new FileResourceOptions
            {
                AutomaticBackupRecovery = true,
                BackupRootDirectory = Path.Combine(directory, "persistent"),
            }
        );

        var recovered = await resource.TryRecoverLatestBackupAsync(
            expectedRevision: null,
            expectedMissing: true,
            static (candidate, _) =>
                ValueTask.FromResult(
                    Encoding.UTF8.GetString(candidate.Content.Span) == "recoverable"
                )
        );

        recovered.HasValue.ShouldBeTrue();
        Encoding.UTF8.GetString(recovered!.Value.Content.Span).ShouldBe("recoverable");
        (await File.ReadAllTextAsync(resourcePath)).ShouldBe("recoverable");
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
