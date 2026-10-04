namespace Configlue.Resources;

public sealed partial class FileResource
{
    /// <summary>Acquires the exclusive in-process lock for this file resource.</summary>
    /// <remarks>Advanced lock primitive for custom journals and migration stores. The lease
    /// serializes same-path writers only within this process; it provides no cross-process mutual
    /// exclusion. Two processes can hold this lease for the same path at the same time, so a
    /// read-condition-write sequence guarded only by this lease remains check-then-act across
    /// processes and can lose updates (see the reliability contract on <see cref="FileResource"/>).
    /// Coordinate externally when cross-process exclusion is required.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public async ValueTask<IDisposable> AcquireExclusiveLockAsync(
        CancellationToken cancellationToken
    )
    {
        var lease = await AcquireProcessLockAsync(_path, cancellationToken).ConfigureAwait(false);
        return new ExclusiveLockLease(lease);
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

    /// <summary>An idempotent holder for an acquired in-process lock.</summary>
    private sealed class ExclusiveLockLease(ProcessLockLease lease) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lease.Dispose();
            }
        }
    }
}
