using System.Diagnostics;

namespace Configlue.Resources;

public sealed partial class FileResource
{
    private readonly object _watchGate = new();
    private readonly TaskCompletionSource _disposedSignal = NewChangeSignal();
    private FileSystemWatcher? _fileWatcher;
    private TaskCompletionSource _changed = NewChangeSignal();
    private bool _disposed;
    private int _disposeCallCount;

    internal bool IsDisposedForTests
    {
        get
        {
            lock (_watchGate)
            {
                return _disposed;
            }
        }
    }

    internal bool HasActiveWatcherForTests
    {
        get
        {
            lock (_watchGate)
            {
                return _fileWatcher is not null;
            }
        }
    }

    internal int DisposeCallCountForTests => Volatile.Read(ref _disposeCallCount);

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        _ = ConfiglueResourceContext.Normalize(context);
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            if (
                !string.Equals(
                    await GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false),
                    observedRevision,
                    StringComparison.Ordinal
                )
            )
            {
                return;
            }

            Task waitTask;
            bool hasWatcher;
            lock (_watchGate)
            {
                if (_disposed)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (_options.ChangeDetectionMode == FileChangeDetectionMode.Hybrid)
                {
                    // Keep watcher creation and signal capture atomic with respect to watcher
                    // callbacks. Otherwise an event can replace _changed after creation but
                    // before this waiter captures it, leaving the waiter on the new signal.
                    EnsureFileWatcher();
                }

                waitTask = _changed.Task;
                hasWatcher = _fileWatcher is not null;
            }

            if (!hasWatcher)
            {
                await PollUntilChangedAsync(observedRevision, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            using var pollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            var watcherTask = waitTask.WaitAsync(pollingCancellation.Token);
            var pollingTask = PollUntilChangedAsync(observedRevision, pollingCancellation.Token);
            var completed = await Task.WhenAny(watcherTask, pollingTask).ConfigureAwait(false);
#if NETSTANDARD
            pollingCancellation.Cancel();
#else
            await pollingCancellation.CancelAsync().ConfigureAwait(false);
#endif
            try
            {
                await completed.ConfigureAwait(false);
            }
            finally
            {
                await ObserveCancellationAsync(completed == watcherTask ? pollingTask : watcherTask)
                    .ConfigureAwait(false);
            }

            if (completed == pollingTask)
            {
                return;
            }
        }
        catch (ObjectDisposedException) when (_disposedSignal.Task.IsCompleted)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCallCount);
        lock (_watchGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            _disposedSignal.TrySetResult();
            _changed.TrySetCanceled();
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

            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(_directory, _fileName)
                {
                    NotifyFilter =
                        NotifyFilters.FileName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size
                        | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                };
                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                watcher.Deleted += OnFileChanged;
                watcher.Renamed += OnFileRenamed;
                watcher.Error += OnWatcherError;
                _fileWatcher = watcher;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception exception)
                when (exception
                        is IOException
                            or UnauthorizedAccessException
                            or ArgumentException
                            or PlatformNotSupportedException
                )
            {
                _fileWatcher = null;
                watcher?.Dispose();
            }
        }
    }

    private async Task PollUntilChangedAsync(
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        var hasSignature = false;
        var lastSignature = default(FileSignature);
        var lastVerifiedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = CaptureFileSignature(_path);
            var signatureChanged = !hasSignature || !signature.Equals(lastSignature);
            var verificationDue =
                Stopwatch.GetTimestamp() - lastVerifiedAt
                >= _options.RevisionVerificationInterval.TotalSeconds * Stopwatch.Frequency;
            if (signatureChanged || verificationDue)
            {
                if (
                    !string.Equals(
                        await GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false),
                        observedRevision,
                        StringComparison.Ordinal
                    )
                )
                {
                    return;
                }
                lastVerifiedAt = Stopwatch.GetTimestamp();
            }

            hasSignature = true;
            lastSignature = signature;
            var remainingVerificationSeconds =
                _options.RevisionVerificationInterval.TotalSeconds
                - (Stopwatch.GetTimestamp() - lastVerifiedAt) / (double)Stopwatch.Frequency;
            var delay = TimeSpan.FromSeconds(
                Math.Min(
                    _options.PollingInterval.TotalSeconds,
                    Math.Max(remainingVerificationSeconds, 0)
                )
            );
            var delayTask = Task.Delay(delay, cancellationToken);
            if (
                await Task.WhenAny(delayTask, _disposedSignal.Task).ConfigureAwait(false)
                != delayTask
            )
            {
                throw new OperationCanceledException(cancellationToken);
            }
            await delayTask.ConfigureAwait(false);
        }
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (task.IsCanceled)
        {
            // The losing wait was canceled after the other path completed.
        }
    }

    private static FileSignature CaptureFileSignature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new FileSignature(info.LastWriteTimeUtc.Ticks, info.Length)
                : default;
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }

    private async ValueTask<string?> GetCurrentRevisionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var content = await ReadFileSnapshotAsync(_path, cancellationToken)
                .ConfigureAwait(false);
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
        catch (UnauthorizedAccessException)
        {
            // On Windows a concurrent replace/delete (or AV scan) can surface as
            // EACCES while the file is briefly locked. Treat it as transiently
            // unreadable, like a sharing violation, so polling keeps waiting.
            return null;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args) => SignalChange();

    private void OnFileRenamed(object sender, RenamedEventArgs args) => SignalChange();

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        lock (_watchGate)
        {
            if (_disposed || !ReferenceEquals(_fileWatcher, sender))
            {
                return;
            }

            _fileWatcher.Dispose();
            _fileWatcher = null;
            var previous = _changed;
            _changed = NewChangeSignal();
            previous.TrySetResult();
        }
    }

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

    private static TaskCompletionSource NewChangeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct FileSignature
    {
        public bool Exists { get; init; }
        public long LastWriteTimeUtcTicks { get; init; }
        public long Length { get; init; }

        public FileSignature(long lastWriteTimeUtcTicks, long length)
        {
            Exists = true;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
            Length = length;
        }
    }
}
