using System.Text;

namespace Configlue.Tests;

public sealed class ResourceBackupRecoveryContractTests
{
    [Test]
    public async Task TryRecoverLatestBackupAsync_LeavesPrimaryUntouchedWhenValidationRejectsBackup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        using var resource = new FileResource(
            path,
            new FileResourceOptions { AutomaticBackupRecovery = true }
        );
        IResourceBackupRecovery recovery = resource;
        await resource.WriteAsync(new ResourceWriteRequest("backup"u8.ToArray()));
        await resource.WriteAsync(new ResourceWriteRequest("current"u8.ToArray()));
        var current = await resource.ReadAsync();
        var validationCalled = false;

        var result = await recovery.TryRecoverLatestBackupAsync(
            current.Revision,
            expectedMissing: false,
            (candidate, _) =>
            {
                validationCalled = true;
                Encoding.UTF8.GetString(candidate.Content.Span).ShouldBe("backup");
                return ValueTaskCompat.FromResult(false);
            }
        );

        validationCalled.ShouldBeTrue();
        result.ShouldBeNull();
        Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span).ShouldBe("current");
    }

    [Test]
    public async Task TryRecoverLatestBackupAsync_RestoresValidatedBackupWhenPrimaryIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        using var resource = new FileResource(
            path,
            new FileResourceOptions { AutomaticBackupRecovery = true }
        );
        IResourceBackupRecovery recovery = resource;
        await resource.WriteAsync(new ResourceWriteRequest("backup"u8.ToArray()));
        await resource.WriteAsync(new ResourceWriteRequest("current"u8.ToArray()));
        File.Delete(path);
        ResourceReadResult? validatedBackup = null;

        var result = await recovery.TryRecoverLatestBackupAsync(
            expectedRevision: null,
            expectedMissing: true,
            (candidate, _) =>
            {
                validatedBackup = candidate;
                return ValueTaskCompat.FromResult(true);
            }
        );

        result.ShouldNotBeNull();
        result.Value.ShouldBe(validatedBackup!.Value);
        Encoding.UTF8.GetString(result.Value.Content.Span).ShouldBe("backup");
        Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span).ShouldBe("backup");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Configlue.Tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
