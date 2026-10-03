using System.Diagnostics;

namespace Configlue.Resources;

public sealed partial class FileResource
{
    private readonly object _watchGate = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private FileSystemWatcher? _fileWatcher;
    private TaskCompletionSource _changed = NewChangeSignal();
    private bool _disposed;
    private int _activeChangeWaits;
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

        if (_options.ChangeDetectionMode == FileChangeDetectionMode.Hybrid)
        {
            EnsureFileWatcher();
        }
        Task waitTask;
        CancellationTokenSource linkedCancellation;
        bool hasWatcher;
        lock (_watchGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            waitTask = _changed.Task;
            hasWatcher = _fileWatcher is not null;
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCancellation.Token
            );
            _activeChangeWaits++;
        }

        try
        {
            using (linkedCancellation)
            {
                try
                {
                    if (
                        !string.Equals(
                            await GetCurrentRevisionAsync(linkedCancellation.Token)
                                .ConfigureAwait(false),
                            observedRevision,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        return;
                    }

                    if (!hasWatcher)
                    {
                        await PollUntilChangedAsync(observedRevision, linkedCancellation.Token)
                            .ConfigureAwait(false);
                        return;
                    }

                    var watcherTask = waitTask.WaitAsync(linkedCancellation.Token);
                    var pollingTask = PollUntilChangedAsync(
                        observedRevision,
                        linkedCancellation.Token
                    );
                    var completed = await Task.WhenAny(watcherTask, pollingTask)
                        .ConfigureAwait(false);
#if NETSTANDARD
                    linkedCancellation.Cancel();
#else
                    await linkedCancellation.CancelAsync().ConfigureAwait(false);
#endif
                    try
                    {
                        await completed.ConfigureAwait(false);
                    }
                    finally
                    {
                        await ObserveCancellationAsync(
                                completed == watcherTask ? pollingTask : watcherTask
                            )
                            .ConfigureAwait(false);
                    }
                }
                catch (ObjectDisposedException) when (linkedCancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException(linkedCancellation.Token);
                }
            }
        }
        finally
        {
            lock (_watchGate)
            {
                _activeChangeWaits--;
                if (_disposed && _activeChangeWaits == 0)
                {
                    _disposeCancellation.Dispose();
                }
            }
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
            _disposeCancellation.Cancel();
            _changed.TrySetCanceled(_disposeCancellation.Token);
            if (_activeChangeWaits == 0)
            {
                _disposeCancellation.Dispose();
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
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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
