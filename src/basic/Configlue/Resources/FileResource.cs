using Configlue.Codecs;
using Configlue.Sources;
using Configlue.State;

namespace Configlue.Resources;

/// <summary>A local file resource with crash-safe replacement, optimistic revision checks, a single backup, and change notifications.</summary>
/// <remarks>
/// <para>Reliability contract for ordinary local settings:</para>
/// <list type="bullet">
/// <item>Writes are crash-safe where the platform supports it: new content is written to a temporary file in the destination directory, flushed to disk, and then atomically published over the target. Readers that already opened the previous file keep their snapshot.</item>
/// <item>Concurrent writers use optimistic concurrency: every read carries a content revision, and a write with a revision condition fails with <see cref="StateConflictException"/> when the file changed after it was read. Same-path writes are additionally serialized within the process; across processes the last revision-checked writer wins.</item>
/// <item>At most one previous-value backup is kept when <see cref="FileResourceOptions.CreateBackup"/> is set: <c>&lt;name&gt;.bak</c> beside the file, or under <see cref="FileResourceOptions.BackupDirectory"/> when configured. Backups are never restored automatically; use <see cref="RestoreLatestBackupAsync"/> to restore one explicitly.</item>
/// <item>Change notifications use a filesystem watcher when one can be created and fall back to polling otherwise. While watching, the content revision is re-verified on every <see cref="FileResourceOptions.PollingInterval"/> tick so missed filesystem events still surface.</item>
/// </list>
/// <para>
/// Policies beyond this contract — multi-generation backup rotation, historical backup discovery,
/// automatic backup recovery, and cross-process lock files — are intentionally not part of the
/// default file path.
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
        : this(path, options, fixedResourceId, null, null) { }

    /// <summary>Creates a file resource using the supplied host profile.</summary>
    /// <remarks>Retained for compatibility; host-specific placement no longer affects the single backup location.</remarks>
    public FileResource(
        string path,
        FileResourceOptions? options,
        ResourceId? fixedResourceId,
        IConfiglueHostPaths hostPaths
    )
        : this(path, options, fixedResourceId, null, hostPaths) { }

    /// <summary>Creates a model-backed file resource at the supplied path.</summary>
    /// <param name="path">The path of the file resource.</param>
    /// <param name="backupSchema">The model identity. Retained for compatibility; it no longer affects the single backup location.</param>
    /// <param name="options">The retry, backup, and change-detection settings.</param>
    /// <param name="fixedResourceId">An optional stable physical identity for the resource.</param>
    public FileResource(
        string path,
        StateSchemaMetadata backupSchema,
        FileResourceOptions? options = null,
        ResourceId? fixedResourceId = null
    )
        : this(path, options, fixedResourceId, backupSchema, null) { }

    /// <summary>Creates a model-backed file resource using host-specific defaults.</summary>
    /// <remarks>Retained for compatibility; model and host placement no longer affect the single backup location.</remarks>
    public FileResource(
        string path,
        StateSchemaMetadata backupSchema,
        FileResourceOptions? options,
        ResourceId? fixedResourceId,
        IConfiglueHostPaths hostPaths
    )
        : this(path, options, fixedResourceId, backupSchema, hostPaths) { }

    private FileResource(
        string path,
        FileResourceOptions? options,
        ResourceId? fixedResourceId,
        StateSchemaMetadata? backupSchema,
        IConfiglueHostPaths? hostPaths
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // The model and host arguments are accepted for compatibility only; the simplified
        // backup contract always resolves a single backup from the options below.
        _ = backupSchema;
        _ = hostPaths;
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
