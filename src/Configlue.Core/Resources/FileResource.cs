using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Configlue;

/// <summary>A local file resource with atomic replacement, revision checks, backups, and change notifications.</summary>
public sealed class FileResource : IResourceReader, IResourceWriter, IStateWatcher, IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string _path;
    private readonly string _directory;
    private readonly string _fileName;
    private readonly FileResourceOptions _options;
    private readonly object _watchGate = new();
    private FileSystemWatcher? _fileWatcher;
    private TaskCompletionSource _changed = NewChangeSignal();
    private bool _disposed;

    /// <summary>Creates a file resource at the supplied path.</summary>
    public FileResource(string path, FileResourceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _directory = System.IO.Path.GetDirectoryName(_path)!;
        _fileName = System.IO.Path.GetFileName(_path);
        _options = options ?? new FileResourceOptions();
        if (_options.RetryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RetryCount cannot be negative.");
        }

        if (_options.RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RetryDelay cannot be negative.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(_options.BackupExtension);
    }

    /// <summary>The normalized file path.</summary>
    public string Path => _path;

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            return ResourceReadResult.Success(content, GetRevision(content));
        }
        catch (FileNotFoundException)
        {
            return ResourceReadResult.NotFound();
        }
        catch (DirectoryNotFoundException)
        {
            return ResourceReadResult.NotFound();
        }
        catch (IOException)
        {
            return ResourceReadResult.Unavailable();
        }
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);

        var processLock = ProcessLocks.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var interprocessLock = await AcquireInterprocessLockAsync(cancellationToken).ConfigureAwait(false);
            var previousContent = await TryReadForWriteAsync(cancellationToken).ConfigureAwait(false);
            var currentRevision = previousContent is null ? null : GetRevision(previousContent);
            if (request.ExpectedRevision is not null && !string.Equals(request.ExpectedRevision, currentRevision, StringComparison.Ordinal))
            {
                throw new StateConflictException($"The file resource '{_path}' changed after it was read.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_options.CreateBackup && previousContent is not null)
            {
                await WriteAtomicAsync(_path + _options.BackupExtension, previousContent, cancellationToken).ConfigureAwait(false);
            }

            var content = request.Content.ToArray();
            await WriteAtomicAsync(_path, content, cancellationToken).ConfigureAwait(false);
            return new StateWriteResult(GetRevision(content));
        }
        finally
        {
            processLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(await GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false), observedRevision, StringComparison.Ordinal))
        {
            return;
        }

        EnsureFileWatcher();
        Task waitTask;
        lock (_watchGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            waitTask = _changed.Task;
        }

        if (!string.Equals(await GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false), observedRevision, StringComparison.Ordinal))
        {
            return;
        }

        if (_fileWatcher is null)
        {
            await PollUntilChangedAsync(observedRevision, waitTask, cancellationToken).ConfigureAwait(false);
            return;
        }

        await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_watchGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            _changed.TrySetCanceled();
        }
    }

    private async ValueTask<FileStream> AcquireInterprocessLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = System.IO.Path.Combine(_directory, "." + _fileName + ".configlue.lock");
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (attempt < _options.RetryCount)
            {
                await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<byte[]?> TryReadForWriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private async ValueTask WriteAtomicAsync(string destinationPath, byte[] content, CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < _options.RetryCount)
                {
                    await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A failed cleanup is harmless; the unique temporary name cannot shadow a later write.
            }
        }
    }

    private void EnsureFileWatcher()
    {
        lock (_watchGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fileWatcher is not null || !Directory.Exists(_directory))
            {
                return;
            }

            var watcher = new FileSystemWatcher(_directory, _fileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnWatcherError;
            _fileWatcher = watcher;
        }
    }

    private async Task PollUntilChangedAsync(string? observedRevision, Task signal, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (signal.IsCompleted || !string.Equals(await GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false), observedRevision, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<string?> GetCurrentRevisionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var content = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            return GetRevision(content);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args) => SignalChange();

    private void OnFileRenamed(object sender, RenamedEventArgs args) => SignalChange();

    private void OnWatcherError(object sender, ErrorEventArgs args) => SignalChange();

    private void SignalChange()
    {
        lock (_watchGate)
        {
            if (_disposed)
            {
                return;
            }

            var previous = _changed;
            _changed = NewChangeSignal();
            previous.TrySetResult();
        }
    }

    private static string GetRevision(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));

    private static TaskCompletionSource NewChangeSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
