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

    private volatile TaskCompletionSource<bool>? _watcherArmedSignalForTests;

    internal TaskCompletionSource<bool>? WatcherArmedSignalForTests
    {
        get => _watcherArmedSignalForTests;
        set => _watcherArmedSignalForTests = value;
    }

    internal void SimulateWatcherErrorForTests()
    {
        lock (_watchGate)
        {
            if (_disposed || _fileWatcher is null)
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
            Task watchSignalTask;
            lock (_watchGate)
            {
                if (_disposed)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (_options.ChangeDetectionMode == FileChangeDetectionMode.Hybrid)
                {
                    EnsureFileWatcher();
                }

                hasWatcher = _fileWatcher is not null;
                // Capture the signal in the same synchronization boundary as watcher
                // creation/state inspection. Otherwise OnWatcherError can dispose the
                // watcher, swap _changed, and complete the previous signal between the
                // two lock regions, leaving this waiter on the fresh signal while the
                // watcher is gone (missed handoff -> polling fallback without a
                // revision change).
                watchSignalTask = _changed.Task;
            }

            // Signal test barrier after the atomic capture so a test can force the
            // error interleaving deterministically: observing the watcher now implies
            // the waiter has already captured the signal it will await.
            _watcherArmedSignalForTests?.TrySetResult(true);

            if (!hasWatcher)
            {
                await PollUntilChangedAsync(observedRevision, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // Filesystem notifications provide low latency while the polling tick re-verifies
            // the content revision, so events missed by the watcher still surface.
            var signalTask = watchSignalTask;
            while (true)
            {
                Task currentSignal;
                lock (_watchGate)
                {
                    if (_disposed)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (_fileWatcher is null)
                    {
                        // The watcher failed while this waiter was between awaits
                        // (for example during the revision re-read below). The
                        // previous signal was already completed by OnWatcherError, so
                        // release the waiter instead of silently falling back to
                        // polling without a revision change.
                        return;
                    }

                    if (signalTask.IsCompleted)
                    {
                        return;
                    }

                    signalTask = _changed.Task;
                    currentSignal = signalTask;
                }

                var tick = Task.Delay(_options.PollingInterval, cancellationToken);
                await Task.WhenAny(currentSignal, tick, _disposedSignal.Task).ConfigureAwait(false);
                if (_disposed || _disposedSignal.Task.IsCompleted)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (currentSignal.IsCompleted)
                {
                    return;
                }

                // Preserve prompt watcher-error handoff when the error lands during
                // the revision re-read below: the re-read await is outside the gate,
                // so check the captured signal again before re-arming.
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

                if (currentSignal.IsCompleted)
                {
                    return;
                }

                // Loop re-enters the gate above, which releases the waiter when the
                // watcher is gone instead of degrading to polling.
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
