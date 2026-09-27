using System.Diagnostics;
using System.Security.Cryptography;

namespace Configlue;

public sealed partial class FileResource
{
    internal async ValueTask<IDisposable> AcquireExclusiveLockAsync(
        CancellationToken cancellationToken
    )
    {
        var processLease = await AcquireProcessLockAsync(_path, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var interprocessLease = await AcquireInterprocessLockAsync(cancellationToken)
                .ConfigureAwait(false);
            return new FileResourceLockLease(processLease, interprocessLease);
        }
        catch
        {
            processLease.Dispose();
            throw;
        }
    }

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

    private static string ResolveLockPath(
        string fullPath,
        string directory,
        string fileName,
        string? lockDirectory
    )
    {
        if (string.Equals(lockDirectory, "/", StringComparison.Ordinal))
        {
            return System.IO.Path.Combine(directory, "." + fileName + ".configlue.lock");
        }

        string targetDirectory;
        if (string.IsNullOrWhiteSpace(lockDirectory))
        {
            targetDirectory = ConfiglueStandardPaths.GetSharedLockDirectory();
        }
        else if (System.IO.Path.IsPathRooted(lockDirectory))
        {
            targetDirectory = System.IO.Path.GetFullPath(lockDirectory);
        }
        else
        {
            targetDirectory = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(directory, lockDirectory)
            );
        }

        var identityPath = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var hash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identityPath))
        );
        var sanitized = SanitizeLockFileSegment(fileName);
        return System.IO.Path.Combine(targetDirectory, $"{sanitized}-{hash}.configlue.lock");
    }

    private static string SanitizeLockFileSegment(string fileName)
    {
        var builder = new System.Text.StringBuilder(fileName.Length);
        foreach (var character in fileName)
        {
            if (char.IsLetterOrDigit(character) || character is '.' or '-' or '_')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('_');
            }
        }

        var sanitized = builder.ToString().Trim('.');
        if (sanitized.Length == 0)
        {
            sanitized = "resource";
        }

        const int maxSegmentLength = 48;
        if (sanitized.Length > maxSegmentLength)
        {
            sanitized = sanitized.Substring(0, maxSegmentLength);
        }

        return sanitized;
    }

    private async ValueTask<FileStream> AcquireInterprocessLockAsync(
        CancellationToken cancellationToken
    )
    {
        var lockDirectory = System.IO.Path.GetDirectoryName(_lockPath);
        if (!string.IsNullOrEmpty(lockDirectory))
        {
            Directory.CreateDirectory(lockDirectory);
        }

        var timeout = _options.LockAcquireTimeout;
        var startTimestamp = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockPath,
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

    private sealed class FileResourceLockLease(
        ProcessLockLease processLease,
        FileStream interprocessLease
    ) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                interprocessLease.Dispose();
            }
            finally
            {
                processLease.Dispose();
            }
        }
    }
}
