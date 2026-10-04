using System.Buffers;
using System.Text;
using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed partial class FileResourceTests
{
    [Test]
    public async Task AtomicReplacement_DoesNotBlockOpenPipelineReaders_AndPreservesTheirSnapshot()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "open-reader.bin");
        var oldContent = Encoding.UTF8.GetBytes("original-snapshot");
        var newContent = Encoding.UTF8.GetBytes("replacement-snapshot");
        await File.WriteAllBytesAsync(path, oldContent);
        using var resource = new FileResource(path);
        var oldRevision = (await resource.ReadAsync()).Revision;
        await using var pipeline = await resource.ReadPipelineAsync();

        var write = await resource.WriteAsync(new ResourceWriteRequest(newContent));
        var original = await pipeline.ReadAllAsync();
        original.ToArray().ShouldBe(oldContent);
        pipeline.Revision.ShouldBe(oldRevision);
        pipeline.Content!.AdvanceTo(original.End);
        var current = await resource.ReadAsync();
        current.Content.ToArray().ShouldBe(newContent);
        current.Revision.ShouldBe(write.Revision);
    }

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
            new FileResourceOptions { CreateBackup = false }
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
            new FileResourceOptions { CreateBackup = false }
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
    public async Task FileResource_KeepsASinglePreviousValueBackup()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(path);

        for (var value = 0; value < 4; value++)
        {
            await resource.WriteAsync(
                new ResourceWriteRequest(Encoding.UTF8.GetBytes($"{{\"value\":{value}}}"))
            );
        }

        (await File.ReadAllTextAsync(path + ".bak")).ShouldBe("{\"value\":2}");
        Directory
            .GetFiles(directory, "*.bak", SearchOption.TopDirectoryOnly)
            .Single()
            .ShouldBe(path + ".bak");
        (await resource.ReadAsync()).Status.ShouldBe(StateReadStatus.Success);
    }

    [Test]
    public async Task FileResource_HonorsACustomBackupDirectory()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(
            path,
            new FileResourceOptions { BackupDirectory = "backups" }
        );

        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("first")));
        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("second")));

        var backupPath = System.IO.Path.Combine(directory, "backups", "settings.json.bak");
        (await File.ReadAllTextAsync(backupPath)).ShouldBe("first");
        File.Exists(path + ".bak").ShouldBeFalse();
    }

    [Test]
    public async Task FileResource_SkipsTheBackupWhenDisabled()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(
            path,
            new FileResourceOptions { CreateBackup = false }
        );

        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("first")));
        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("second")));

        File.Exists(path + ".bak").ShouldBeFalse();
    }

    [Test]
    public async Task FileResource_RestoresTheSingleBackupExplicitly()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(path);

        await Should.ThrowAsync<FileNotFoundException>(async () =>
            await resource.RestoreLatestBackupAsync()
        );
        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("first")));
        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("second")));

        var restored = await resource.RestoreLatestBackupAsync();
        var current = await resource.ReadAsync();

        (restored.Revision).ShouldBe(current.Revision);
        (Encoding.UTF8.GetString(current.Content.Span)).ShouldBe("first");
    }

    [Test]
    public async Task SerializedReader_LeavesRecoveryDisabledByDefault()
    {
        var directory = CreateTemporaryDirectory();
        using var cleanup = new DirectoryCleanup(directory);
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(path);
        await File.WriteAllTextAsync(path + ".bak", "{}");
        var reader = new SerializedStateReader<AppSettings.Fragment>(
            resource,
            new JsonStateCodec<AppSettings.Fragment>()
        );

        var result = await reader.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.NotFound);
        File.Exists(path).ShouldBeFalse();
    }

    [Test]
    public async Task SerializedReader_LeavesPrimaryUntouchedWhenBackupIsInvalid()
    {
        var directory = CreateTemporaryDirectory();
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var resource = new FileResource(path);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "{ invalid primary");
        var corruptPrimary = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path + ".bak", "{ invalid backup");
        var reader = new SerializedStateReader<AppSettings.Fragment>(
            resource,
            new JsonStateCodec<AppSettings.Fragment>()
        );

        await Should.ThrowAsync<JsonException>(async () => await reader.ReadAsync());

        (await File.ReadAllTextAsync(path)).ShouldBe(corruptPrimary);
    }

    private static string CreateTemporaryDirectory() =>
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );

    private static string GetDefaultBackupPath(string path) => path + ".bak";

    private static void DeleteDirectory(string directory)
    {
        // FileSystemWatcher keeps the watched directory handle open on Windows and
        // releases it asynchronously after Dispose, so cleanup races teardown with
        // EACCES. Retry with backoff instead of failing the test during cleanup.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                ClearReadOnlyAttributes(directory);
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        try
        {
            foreach (
                var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            )
            {
                try
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
