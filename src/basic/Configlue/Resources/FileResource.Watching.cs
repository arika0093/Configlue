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

                hasWatcher = _fileWatcher is not null;
            }

            if (!hasWatcher)
            {
                await PollUntilChangedAsync(observedRevision, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // Filesystem notifications provide low latency while the polling tick re-verifies
            // the content revision, so events missed by the watcher still surface.
            while (true)
            {
                Task signalTask;
                lock (_watchGate)
                {
                    if (_disposed)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (_fileWatcher is null)
                    {
                        break;
                    }

                    signalTask = _changed.Task;
                }

                var tick = Task.Delay(_options.PollingInterval, cancellationToken);
                var completed = await Task.WhenAny(signalTask, tick, _disposedSignal.Task)
                    .ConfigureAwait(false);
                if (_disposed || _disposedSignal.Task.IsCompleted)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (ReferenceEquals(completed, signalTask))
                {
                    return;
                }

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
            }

            // The watcher became unavailable after an error; fall back to polling.
            await PollUntilChangedAsync(observedRevision, cancellationToken).ConfigureAwait(false);
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
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            var delay = Task.Delay(_options.PollingInterval, cancellationToken);
            if (await Task.WhenAny(delay, _disposedSignal.Task).ConfigureAwait(false) != delay)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            await delay.ConfigureAwait(false);
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
}
