using System.Diagnostics;
using System.Security.Cryptography;

namespace Configlue;

/// <summary>A local file resource with atomic replacement, revision checks, backups, and change notifications.</summary>
/// <remarks>
/// <para>
/// Writes to the same normalized path are serialized both within the process, by a reference-counted
/// semaphore that is removed once the last owner or waiter leaves, and across processes, by a zero-byte
/// sidecar file named <c>.&lt;filename&gt;.configlue.lock</c> opened with exclusive sharing.
/// </para>
/// <para>
/// The sidecar file is intentionally persistent: it is created on first use and never deleted. Deleting
/// it on release would let a second process recreate the same path as a different file while an earlier
/// holder is still using it, bypassing the lock, so the marker is left in place. There is at most one
/// sidecar per target path, so it does not grow with the number of writes.
/// </para>
/// <para>
/// Cross-process lock contention waits until <see cref="FileResourceOptions.LockAcquireTimeout"/> elapses
/// or the operation's cancellation token is signaled; it is not limited by the transient-I/O retry
/// settings. The default timeout waits indefinitely so a healthy same-path writer is never failed
/// spuriously.
/// </para>
/// </remarks>
public sealed class FileResource
    : IResourceReader,
        IStateWatcher,
        IResourceBatchWriter,
        IResourceBackupRecovery,
        IDisposable
{
    private static readonly object ProcessLockGate = new();
    private static readonly Dictionary<string, ProcessLockEntry> ProcessLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
    );

    private readonly string _path;
    private readonly string _directory;
    private readonly string _fileName;
    private readonly string _backupDirectory;
    private readonly FileResourceOptions _options;
    private readonly object _watchGate = new();
    private FileSystemWatcher? _fileWatcher;
    private TaskCompletionSource _changed = NewChangeSignal();
    private bool _disposed;
    private int _disposeCallCount;

    /// <summary>Creates a file resource at the supplied path.</summary>
    public FileResource(
        string path,
        FileResourceOptions? options = null,
        ResourceId? resourceId = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        var identityPath = OperatingSystem.IsWindows() ? _path.ToUpperInvariant() : _path;
        ResourceId = resourceId ?? new ResourceId($"file:{identityPath}");
        _directory = System.IO.Path.GetDirectoryName(_path)!;
        _fileName = System.IO.Path.GetFileName(_path);
        _options = options ?? new FileResourceOptions();
        _backupDirectory = _options.BackupDirectory is null
            ? _directory
            : System.IO.Path.GetFullPath(_options.BackupDirectory);
        if (_options.BackupMaxCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "BackupMaxCount cannot be negative."
            );
        }

        if (_options.RetryCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "RetryCount cannot be negative."
            );
        }

        if (_options.RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "RetryDelay cannot be negative."
            );
        }

        if (
            _options.LockAcquireTimeout.HasValue
            && _options.LockAcquireTimeout.Value < TimeSpan.Zero
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "LockAcquireTimeout cannot be negative."
            );
        }

        if (_options.LockAcquireRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "LockAcquireRetryDelay cannot be negative."
            );
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(_options.BackupExtension);
    }

    /// <summary>The normalized file path.</summary>
    public string Path => _path;

    /// <inheritdoc />
    public ResourceId ResourceId { get; }

    /// <inheritdoc />
    public bool AutomaticBackupRecoveryEnabled => _options.AutomaticBackupRecovery;

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
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var content = await File.ReadAllBytesAsync(_path, cancellationToken)
                .ConfigureAwait(false);
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
    public ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => WriteBatchAsync([ResourceWriteMutation.Replace(request)], cancellationToken);

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteBatchAsync(
        IReadOnlyList<ResourceWriteMutation> mutations,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResourceWriteMutation.ValidateBatch(mutations);
        Directory.CreateDirectory(_directory);

        var processLock = await AcquireProcessLockAsync(_path, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var interprocessLock = await AcquireInterprocessLockAsync(cancellationToken)
                .ConfigureAwait(false);
            var previousContent = await TryReadForWriteAsync(cancellationToken)
                .ConfigureAwait(false);
            var currentRevision = previousContent is null ? null : GetRevision(previousContent);
            var expectedRevision = mutations[0].ExpectedRevision;
            var checkRevision = mutations.Any(static mutation =>
                mutation.CheckRevision || mutation.ExpectedRevision is not null
            );
            if (
                checkRevision
                && !string.Equals(expectedRevision, currentRevision, StringComparison.Ordinal)
            )
            {
                throw new StateConflictException(
                    $"The file resource '{_path}' changed after it was read."
                );
            }

            var content = ApplyMutations(mutations, previousContent, currentRevision);
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.CreateBackup && _options.BackupMaxCount > 0 && previousContent is not null)
            {
                await CreateBackupAsync(previousContent, cancellationToken).ConfigureAwait(false);
            }

            await WriteAtomicAsync(_path, content, cancellationToken).ConfigureAwait(false);
            return new StateWriteResult(GetRevision(content));
        }
        finally
        {
            processLock.Dispose();
        }
    }

    private static byte[] ApplyMutations(
        IReadOnlyList<ResourceWriteMutation> mutations,
        byte[]? previousContent,
        string? revision
    )
    {
        var current = previousContent is null
            ? ResourceReadResult.NotFound(revision)
            : ResourceReadResult.Success(previousContent, revision);
        foreach (var mutation in mutations)
        {
            var content = mutation.Apply(current).ToArray();
            current = ResourceReadResult.Success(content, revision);
        }

        return current.Content.ToArray();
    }

    /// <summary>Restores the latest backup without creating another backup generation.</summary>
    /// <exception cref="FileNotFoundException">No latest backup exists.</exception>
    public async ValueTask<StateWriteResult> RestoreLatestBackupAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        var processLock = await AcquireProcessLockAsync(_path, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var interprocessLock = await AcquireInterprocessLockAsync(cancellationToken)
                .ConfigureAwait(false);
            var backupPath = GetBackupPath(0);
            var content = await File.ReadAllBytesAsync(backupPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteAtomicAsync(_path, content, cancellationToken).ConfigureAwait(false);
            return new StateWriteResult(GetRevision(content));
        }
        finally
        {
            processLock.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
        string? expectedRevision,
        bool expectedMissing,
        Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(validate);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        var processLock = await AcquireProcessLockAsync(_path, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var interprocessLock = await AcquireInterprocessLockAsync(cancellationToken)
                .ConfigureAwait(false);
            var current = await TryReadForWriteAsync(cancellationToken).ConfigureAwait(false);
            var currentRevision = current is null ? null : GetRevision(current);
            if (
                expectedMissing != (current is null)
                || !string.Equals(expectedRevision, currentRevision, StringComparison.Ordinal)
            )
            {
                throw new StateConflictException(
                    $"The file resource '{_path}' changed while backup recovery was being prepared."
                );
            }

            byte[] backup;
            try
            {
                backup = await File.ReadAllBytesAsync(GetBackupPath(0), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }

            var backupResult = ResourceReadResult.Success(backup, GetRevision(backup));
            if (!await validate(backupResult, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            await WriteAtomicAsync(_path, backup, cancellationToken).ConfigureAwait(false);
            return backupResult;
        }
        finally
        {
            processLock.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
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

        EnsureFileWatcher();
        Task waitTask;
        lock (_watchGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            waitTask = _changed.Task;
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

        if (_fileWatcher is null)
        {
            await PollUntilChangedAsync(observedRevision, waitTask, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _changed.TrySetCanceled();
        }
    }

    /// <summary>
    /// Acquires the in-process per-path lock. The entry is reference counted so that it can be removed
    /// from <see cref="ProcessLocks"/> once the last owner or waiter leaves, keeping the dictionary
    /// bounded by the number of paths in flight rather than the number of paths ever seen.
    /// </summary>
    /// <remarks>
    /// Every owner and waiter increments the count while holding <see cref="ProcessLockGate"/> before it
    /// touches the semaphore, and only decrements after it has released (or failed to acquire) it.
    /// Removal happens under the same gate and only when the count reaches zero, so a concurrent acquirer
    /// either observes the removed entry and creates a fresh one or has already incremented the count and
    /// keeps the entry alive. No waiter can be left holding a semaphore that is no longer reachable from
    /// the dictionary.
    /// </remarks>
    private static async ValueTask<ProcessLockLease> AcquireProcessLockAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        ProcessLockEntry entry;
        lock (ProcessLockGate)
        {
            if (!ProcessLocks.TryGetValue(path, out entry!))
            {
                entry = new ProcessLockEntry();
                ProcessLocks.Add(path, entry);
            }

            entry.ReferenceCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseProcessLock(path, entry, releaseSemaphore: false);
            throw;
        }

        return new ProcessLockLease(path, entry);
    }

    private static void ReleaseProcessLock(
        string path,
        ProcessLockEntry entry,
        bool releaseSemaphore
    )
    {
        lock (ProcessLockGate)
        {
            entry.ReferenceCount--;
            if (
                entry.ReferenceCount == 0
                && ProcessLocks.TryGetValue(path, out var current)
                && ReferenceEquals(current, entry)
            )
            {
                ProcessLocks.Remove(path);
            }
        }

        if (releaseSemaphore)
        {
            entry.Semaphore.Release();
        }
    }

    /// <summary>The number of live per-path lock entries. Exposed for tests to assert bounded growth.</summary>
    internal static int ProcessLockCount
    {
        get
        {
            lock (ProcessLockGate)
            {
                return ProcessLocks.Count;
            }
        }
    }

    /// <summary>Whether a lock entry still exists for the supplied normalized path. Exposed for tests.</summary>
    internal static bool HasProcessLockFor(string path)
    {
        lock (ProcessLockGate)
        {
            return ProcessLocks.ContainsKey(path);
        }
    }

    private async ValueTask<FileStream> AcquireInterprocessLockAsync(
        CancellationToken cancellationToken
    )
    {
        var lockPath = System.IO.Path.Combine(_directory, "." + _fileName + ".configlue.lock");
        var timeout = _options.LockAcquireTimeout;
        var startTimestamp = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough
                );
            }
            catch (IOException)
                when (timeout is null || Stopwatch.GetElapsedTime(startTimestamp) < timeout.Value)
            {
                await Task.Delay(_options.LockAcquireRetryDelay, cancellationToken)
                    .ConfigureAwait(false);
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

    private async ValueTask CreateBackupAsync(
        byte[] previousContent,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(_backupDirectory);
        for (var index = _options.BackupMaxCount - 1; index > 0; index--)
        {
            var previousBackupPath = GetBackupPath(index - 1);
            byte[]? olderContent;
            try
            {
                olderContent = await File.ReadAllBytesAsync(previousBackupPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            await WriteAtomicAsync(GetBackupPath(index), olderContent, cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteAtomicAsync(GetBackupPath(0), previousContent, cancellationToken)
            .ConfigureAwait(false);
    }

    private string GetBackupPath(int index)
    {
        var backupName = _fileName + _options.BackupExtension;
        if (index > 0)
        {
            backupName += "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return System.IO.Path.Combine(_backupDirectory, backupName);
    }

    private async ValueTask WriteAtomicAsync(
        string destinationPath,
        byte[] content,
        CancellationToken cancellationToken
    )
    {
        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (
                var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough
                )
            )
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            var attempt = 0;
            while (true)
            {
                try
                {
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < _options.RetryCount)
                {
                    attempt++;
                    var retryDelayFactory = _options.RetryDelayFactory;
                    var retryDelay = _options.RetryDelay;
                    if (retryDelayFactory is not null)
                    {
                        retryDelay = retryDelayFactory(attempt);
                    }

                    if (retryDelay < TimeSpan.Zero)
                    {
                        throw new InvalidOperationException(
                            "The retry delay factory returned a negative delay."
                        );
                    }

                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
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
                NotifyFilter =
                    NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
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

    private async Task PollUntilChangedAsync(
        string? observedRevision,
        Task signal,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                signal.IsCompleted
                || !string.Equals(
                    await GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false),
                    observedRevision,
                    StringComparison.Ordinal
                )
            )
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<string?> GetCurrentRevisionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var content = await File.ReadAllBytesAsync(_path, cancellationToken)
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

    private static string GetRevision(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content));

    private static TaskCompletionSource NewChangeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A per-path lock entry with the number of current owners and waiters.</summary>
    private sealed class ProcessLockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }

    /// <summary>Releases the reference counted per-path lock when the operation completes.</summary>
    private readonly struct ProcessLockLease : IDisposable
    {
        private readonly string _path;
        private readonly ProcessLockEntry _entry;

        public ProcessLockLease(string path, ProcessLockEntry entry)
        {
            _path = path;
            _entry = entry;
        }

        public void Dispose() => ReleaseProcessLock(_path, _entry, releaseSemaphore: true);
    }
}
