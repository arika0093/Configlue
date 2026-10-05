using Configlue.Codecs;
using Configlue.Sources;
using Configlue.State;

namespace Configlue.Resources;

/// <summary>A local file resource with crash-safe replacement, optimistic revision checks, a single backup, and change notifications.</summary>
/// <remarks>
/// <para>Reliability contract for ordinary local settings:</para>
/// <list type="bullet">
/// <item>Writes are crash-safe where the platform supports it: new content is written to a temporary file in the destination directory, flushed to disk, and then atomically published over the target. Readers that already opened the previous file keep their snapshot.</item>
/// <item>Same-path writes are serialized only within this process. Across processes there is no mutual exclusion: the read-condition-write sequence is check-then-act, so two processes can read the same revision, both satisfy a revision condition, and both publish. The second publisher overwrites the first without a <see cref="StateConflictException"/> (lost update). A revision condition therefore guarantees conflict detection only for in-process concurrency and for sequential cross-process use; concurrent cross-process conditional writes are last-writer-wins, as unconditional writes always are. Processes that need cross-process exclusion must coordinate externally (for example a single writer process or an OS-level file lock).</item>
/// <item>At most one previous-value backup is kept when <see cref="FileResourceOptions.CreateBackup"/> is set: <c>&lt;name&gt;.bak</c> beside the file, or under <see cref="FileResourceOptions.BackupDirectory"/> when configured. The single backup holds only the most recently replaced content; it is not a history. Cross-process concurrent writes can interleave backup and main-file replacement, so the backup may capture either writer's predecessor rather than a linear latest. Only the resolved single path is read or restored; multi-generation rotation and legacy backup locations are not discovered. Backups are never restored automatically; use <see cref="RestoreLatestBackupAsync"/> to restore one explicitly.</item>
/// <item>Change notifications use a filesystem watcher when one can be created and fall back to polling otherwise. While watching, the content revision is re-verified on every <see cref="FileResourceOptions.PollingInterval"/> tick so missed filesystem events still surface. Each tick reads the full file content to compute its revision, so polling costs O(file size) I/O per interval with no signature fast-path; increase <see cref="FileResourceOptions.PollingInterval"/> for large files or wait-heavy scenarios.</item>
/// </list>
/// <para>
/// Policies beyond this contract — multi-generation backup rotation, historical backup discovery,
/// automatic backup recovery, revision-verification throttling, and cross-process lock files — are
/// intentionally not part of the default file path.
/// </para>
/// <para>
/// Breaking changes from the previous generation, which callers relying on the removed policies must
/// migrate from explicitly: <c>AutomaticBackupRecovery</c> and <c>TryRecoverLatestBackupAsync</c>
/// (including the <c>IResourceBackupRecovery</c> surface) are gone, so corrupt or missing input is
/// never repaired automatically — validate the content and call
/// <see cref="RestoreLatestBackupAsync"/> explicitly instead. <c>BackupMaxCount</c>,
/// <c>BackupDirectoryMode</c>, <c>BackupRootDirectory</c>, and related placement options are gone;
/// only the single backup above is kept. <c>RevisionVerificationInterval</c> and the lightweight
/// signature fast-path are gone; every polling tick is a full content read. The cross-process
/// sidecar lock (<c>LockDirectory</c>, <c>LockAcquireTimeout</c>) is gone; same-path exclusion is
/// in-process only as described above.
/// </para>
/// </remarks>
/// <remarks>Advanced resource: ordinary application code uses provider file-source helpers instead of
/// constructing this type directly.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed partial class FileResource
    : IResourceReader,
        IPipelineResourceReader,
        ISourceWatcher,
        IResourceBatchWriter,
        IDisposable
{
    private static readonly object ProcessLockGate = new();
    private static readonly Dictionary<string, ProcessLockEntry> ProcessLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
    );

    private readonly string _path;
    private readonly string _directory;
    private readonly string _fileName;
    private readonly string _backupPath;
    private readonly FileResourceOptions _options;

    /// <summary>Creates a file resource at the supplied path.</summary>
    /// <param name="path">The path of the file resource.</param>
    /// <param name="options">The retry, backup, and change-detection settings.</param>
    /// <param name="fixedResourceId">An optional stable physical identity for the resource.</param>
    public FileResource(
        string path,
        FileResourceOptions? options = null,
        ResourceId? fixedResourceId = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        var identityPath = OperatingSystem.IsWindows() ? _path.ToUpperInvariant() : _path;
        ResourceId = fixedResourceId ?? new ResourceId($"file:{identityPath}");
        _directory = System.IO.Path.GetDirectoryName(_path)!;
        _fileName = System.IO.Path.GetFileName(_path);
        _options = options ?? new FileResourceOptions();
        if (!Enum.IsDefined(typeof(FileChangeDetectionMode), _options.ChangeDetectionMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "ChangeDetectionMode is invalid."
            );
        }

        if (
            _options.PollingInterval <= TimeSpan.Zero
            || _options.PollingInterval.TotalMilliseconds > int.MaxValue
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "PollingInterval must be greater than zero and at most Int32.MaxValue milliseconds."
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

        ArgumentException.ThrowIfNullOrWhiteSpace(_options.BackupExtension);
        _backupPath = ResolveBackupPath(
            _directory,
            _fileName,
            _options.BackupDirectory,
            _options.BackupExtension
        );
    }

    /// <summary>The normalized file path.</summary>
    public string Path => _path;

    /// <summary>The resolved single-backup path. Exposed for tests.</summary>
    internal string BackupPathForTests => _backupPath;

    /// <inheritdoc />
    public ResourceId ResourceId { get; }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context)
    {
        _ = context;
        return ResourceId;
    }

    /// <inheritdoc />
    public bool IsPipelineReadPreferred => true;

    /// <inheritdoc />
    public ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        _ = ConfiglueResourceContext.Normalize(context);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
#if NETSTANDARD
            var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
#else
            var stream = new FileStream(
                _path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read | FileShare.Delete,
                    BufferSize = 81920,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                }
            );
#endif
            return new ValueTask<PipelineResourceReadResult>(
                PipelineResourceReader.FromStream(stream, computeXxHash3Revision: true)
            );
        }
        catch (FileNotFoundException)
        {
            return new ValueTask<PipelineResourceReadResult>(PipelineResourceReadResult.NotFound());
        }
        catch (DirectoryNotFoundException)
        {
            return new ValueTask<PipelineResourceReadResult>(PipelineResourceReadResult.NotFound());
        }
        catch (IOException)
        {
            return new ValueTask<PipelineResourceReadResult>(
                PipelineResourceReadResult.Unavailable()
            );
        }
        catch (UnauthorizedAccessException)
        {
            return new ValueTask<PipelineResourceReadResult>(
                PipelineResourceReadResult.Unavailable()
            );
        }
    }

    private static string ResolveBackupPath(
        string directory,
        string fileName,
        string? backupDirectory,
        string backupExtension
    )
    {
        if (
            backupDirectory is null
            || string.Equals(backupDirectory, "/", StringComparison.Ordinal)
        )
        {
            return System.IO.Path.Combine(directory, fileName + backupExtension);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        var target = System.IO.Path.IsPathRooted(backupDirectory)
            ? System.IO.Path.GetFullPath(backupDirectory)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, backupDirectory));
        return System.IO.Path.Combine(target, fileName + backupExtension);
    }

    private static string GetRevision(ReadOnlySpan<byte> content) =>
        ConfiglueHashing.GetXxHash3Hex(content);
}
