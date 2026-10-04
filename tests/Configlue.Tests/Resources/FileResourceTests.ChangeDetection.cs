#if NET10_0
using System.Reflection;

namespace Configlue.Tests;

public sealed partial class FileResourceTests
{
    [Test]
    [Arguments(FileChangeDetectionMode.Hybrid)]
    [Arguments(FileChangeDetectionMode.Polling)]
    public async Task ChangeDetectionCancellationDoesNotCancelOtherCallers(
        FileChangeDetectionMode mode
    )
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "cancel.txt");
        await File.WriteAllTextAsync(path, "old");
        using var resource = new FileResource(
            path,
            new FileResourceOptions
            {
                ChangeDetectionMode = mode,
                PollingInterval = TimeSpan.FromMilliseconds(20),
            }
        );
        var revision = (await resource.ReadAsync()).Revision;
        using var cancel = new CancellationTokenSource();
        var first = resource.WaitForChangeAsync(default, revision, cancel.Token).AsTask();
        var second = resource.WaitForChangeAsync(default, revision).AsTask();
        await Task.Delay(100);
        cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => first);
        second.IsCompleted.ShouldBeFalse();
        await WriteTextWithRetryAsync(path, "new");
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    [Arguments(FileChangeDetectionMode.Hybrid)]
    [Arguments(FileChangeDetectionMode.Polling)]
    public async Task DisposalCancelsAllPendingChangeDetectionWaits(FileChangeDetectionMode mode)
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "dispose.txt");
        await File.WriteAllTextAsync(path, "old");
        using var resource = new FileResource(
            path,
            new FileResourceOptions { ChangeDetectionMode = mode }
        );
        var revision = (await resource.ReadAsync()).Revision;
        var waits = Enumerable
            .Range(0, 4)
            .Select(_ => resource.WaitForChangeAsync(default, revision).AsTask())
            .ToArray();
        await Task.Delay(100);
        resource.Dispose();
        foreach (var wait in waits)
            await Should.ThrowAsync<OperationCanceledException>(() =>
                wait.WaitAsync(TimeSpan.FromSeconds(5))
            );
    }

    [Test]
    public async Task HybridPollsAfterWatcherSuccessAndDetectsSameMetadataRewrite()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "missed.txt");
        await File.WriteAllTextAsync(path, "old");
        using var resource = new FileResource(
            path,
            new FileResourceOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(100),
            }
        );
        var revision = (await resource.ReadAsync()).Revision;
        var first = resource.WaitForChangeAsync(default, revision).AsTask();
        var watcher = await GetReviewWatcher(resource);
        await WriteTextWithRetryAsync(path, "one");
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        watcher.EnableRaisingEvents = false;
        await Task.Delay(150);
        revision = (await resource.ReadAsync()).Revision;
        var second = resource.WaitForChangeAsync(default, revision).AsTask();
        second.IsCompleted.ShouldBeFalse();
        var timestamp = File.GetLastWriteTimeUtc(path);
        await WriteTextWithRetryAsync(path, "two");
        File.SetLastWriteTimeUtc(path, timestamp);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        resource.HasActiveWatcherForTests.ShouldBeTrue();
    }

    [Test]
    public async Task WatcherCompletesBeforeLongPollingInterval()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "watcher.txt");
        await WriteTextWithRetryAsync(path, "old");
        using var resource = new FileResource(
            path,
            new FileResourceOptions { PollingInterval = TimeSpan.FromSeconds(30) }
        );
        var revision = (await resource.ReadAsync()).Revision;
        var wait = resource.WaitForChangeAsync(default, revision).AsTask();
        await GetReviewWatcher(resource);
        await Task.Delay(150);
        // Same content makes polling unable to detect this notification.
        await WriteTextWithRetryAsync(path, "old");
        // File-system events can lag on loaded runners; 15s still proves the
        // event path well before the 30s polling interval above.
        await wait.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Test]
    [Arguments(FileChangeDetectionMode.Hybrid)]
    [Arguments(FileChangeDetectionMode.Polling)]
    public async Task PollingDetectsCreateReplaceAndDeleteInInitiallyMissingDirectory(
        FileChangeDetectionMode mode
    )
    {
        var directory = CreateTemporaryDirectory();
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "create.txt");
        using var resource = new FileResource(
            path,
            new FileResourceOptions
            {
                ChangeDetectionMode = mode,
                PollingInterval = TimeSpan.FromMilliseconds(20),
            }
        );
        var create = resource.WaitForChangeAsync(default, null).AsTask();
        await Task.Delay(100);
        Directory.CreateDirectory(directory);
        await WriteTextWithRetryAsync(path, "old");
        await create.WaitAsync(TimeSpan.FromSeconds(15));
        if (mode == FileChangeDetectionMode.Polling)
            resource.HasActiveWatcherForTests.ShouldBeFalse();
        var replace = resource
            .WaitForChangeAsync(default, (await resource.ReadAsync()).Revision)
            .AsTask();
        await Task.Delay(100);
        var temporary = path + ".tmp";
        await WriteTextWithRetryAsync(temporary, "replacement");
        await MoveWithRetryAsync(temporary, path);
        await replace.WaitAsync(TimeSpan.FromSeconds(15));
        var delete = resource
            .WaitForChangeAsync(default, (await resource.ReadAsync()).Revision)
            .AsTask();
        await Task.Delay(100);
        await DeleteWithRetryAsync(path);
        await delete.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Test]
    public void ChangeDetectionOptionsRejectInvalidDurationsAndModes()
    {
        foreach (
            var options in new[]
            {
                new FileResourceOptions { PollingInterval = TimeSpan.Zero },
                new FileResourceOptions { PollingInterval = TimeSpan.FromMilliseconds(-1) },
                new FileResourceOptions { PollingInterval = TimeSpan.MaxValue },
                new FileResourceOptions { ChangeDetectionMode = (FileChangeDetectionMode)123 },
            }
        )
            Should.Throw<ArgumentOutOfRangeException>(() =>
                new FileResource("invalid.txt", options)
            );
    }

    [Test]
    public async Task WatcherErrorUnblocksWaitAndFollowingWaitRecreatesWatcher()
    {
        var directory = CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var path = System.IO.Path.Combine(directory, "error.txt");
        await File.WriteAllTextAsync(path, "old");
        using var resource = new FileResource(path);
        var revision = (await resource.ReadAsync()).Revision;
        var wait = resource.WaitForChangeAsync(default, revision).AsTask();
        var watcher = await GetReviewWatcher(resource);
        typeof(FileResource)
            .GetMethod("OnWatcherError", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                resource,
                new object[] { watcher, new ErrorEventArgs(new IOException("simulated overflow")) }
            );
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        resource.HasActiveWatcherForTests.ShouldBeFalse();
        using var cancellation = new CancellationTokenSource();
        var next = resource.WaitForChangeAsync(default, revision, cancellation.Token).AsTask();
        (await GetReviewWatcher(resource)).ShouldNotBeSameAs(watcher);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => next);
    }

    private static async Task<FileSystemWatcher> GetReviewWatcher(FileResource resource)
    {
        var field = typeof(FileResource).GetField(
            "_fileWatcher",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (field.GetValue(resource) is FileSystemWatcher watcher)
                return watcher;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Watcher was not created.");
    }

    private static async Task WriteTextWithRetryAsync(string path, string content)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await File.WriteAllTextAsync(path, content);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(10);
            }
            catch (UnauthorizedAccessException) when (attempt < 50)
            {
                // On Windows a concurrent polling read or AV scan can hold the
                // file briefly, surfacing as EACCES instead of a sharing violation.
                await Task.Delay(10);
            }
        }
    }

    private static async Task MoveWithRetryAsync(string sourcePath, string destinationPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                // The polling reader opens the destination with
                // FileShare.Read | FileShare.Delete and closes it quickly;
                // File.Move (unlike File.Replace) fails while that read
                // handle is open, so retry with backoff.
                await Task.Delay(10);
            }
            catch (UnauthorizedAccessException) when (attempt < 50)
            {
                await Task.Delay(10);
            }
        }
    }

    private static async Task DeleteWithRetryAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(10);
            }
            catch (UnauthorizedAccessException) when (attempt < 50)
            {
                await Task.Delay(10);
            }
        }
    }
}

#endif
