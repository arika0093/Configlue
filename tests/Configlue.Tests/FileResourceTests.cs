using System.Text;

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
            var backup = await File.ReadAllBytesAsync(path + ".bak");
            var saved = await resource.ReadAsync();

            await Assert.That(missing.Status).IsEqualTo(StateReadStatus.NotFound);
            await Assert.That(current.Status).IsEqualTo(StateReadStatus.Success);
            await Assert.That(current.Revision).IsEqualTo(firstWrite.Revision);
            await Assert.That(missingRevisionConflict).IsTrue();
            await Assert.That(conflict).IsTrue();
            await Assert.That(secondWrite.Revision).IsNotEqualTo(firstWrite.Revision);
            await Assert.That(Encoding.UTF8.GetString(backup)).IsEqualTo("{\"value\":1}");
            await Assert
                .That(Encoding.UTF8.GetString(saved.Content.Span))
                .IsEqualTo("{\"value\":2}");
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

            await Assert.That(latestBackup).IsEqualTo("{\"value\":2}");
            await Assert.That(olderBackup).IsEqualTo("{\"value\":1}");
            await Assert.That(restored.Revision).IsEqualTo(current.Revision);
            await Assert
                .That(Encoding.UTF8.GetString(current.Content.Span))
                .IsEqualTo("{\"value\":2}");
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
}
