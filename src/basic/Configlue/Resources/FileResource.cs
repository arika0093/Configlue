using System.Diagnostics;
using System.Runtime.InteropServices;
using Configlue.Codecs;
using Configlue.Sources;
using Configlue.State;

namespace Configlue.Resources;

/// <summary>A local file resource with atomic replacement, revision checks, backups, and change notifications.</summary>
/// <remarks>
/// <para>
/// Writes to the same normalized path are serialized both within the process, by a reference-counted
/// semaphore that is removed once the last owner or waiter leaves, and across processes, by a zero-byte
/// sidecar lock file opened with exclusive sharing.
/// </para>
/// <para>
/// The sidecar file is intentionally persistent: it is created on first use and never deleted. Deleting
/// it on release would let a second process recreate the same path as a different file while an earlier
/// holder is still using it, bypassing the lock, so the marker is left in place. There is at most one
/// sidecar per target path, so it does not grow with the number of writes. By default the sidecar lives
/// under <see cref="ConfiglueStandardPaths.GetSharedLockDirectory"/> instead of beside the target file;
/// set <see cref="FileResourceOptions.LockDirectory"/> to <c>/</c> to restore the legacy co-located
/// <c>.&lt;filename&gt;.configlue.lock</c> behavior.
/// </para>
/// <para>
/// Cross-process lock contention waits until <see cref="FileResourceOptions.LockAcquireTimeout"/> elapses
/// or the operation's cancellation token is signaled; it is not limited by the transient-I/O retry
/// settings. The default timeout waits indefinitely so a healthy same-path writer is never failed
/// spuriously.
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
    private readonly string _backupFileName;
    private readonly string _lockPath;
    private readonly string _backupDirectory;
    private readonly string[] _previousBackupDirectories;
    private readonly FileResourceOptions _options;
    private readonly IConfiglueHostPaths _hostPaths;

    /// <summary>Creates a file resource at the supplied path.</summary>
    /// <param name="path">The path of the file resource.</param>
    /// <param name="options">The retry and backup settings.</param>
    /// <param name="fixedResourceId">An optional stable physical identity for the resource.</param>
    public FileResource(
        string path,
        FileResourceOptions? options = null,
        ResourceId? fixedResourceId = null
    )
        : this(path, options, fixedResourceId, null, null) { }

    /// <summary>Creates a file resource using the supplied host profile for default backups.</summary>
    public FileResource(
        string path,
        FileResourceOptions? options,
        ResourceId? fixedResourceId,
        IConfiglueHostPaths hostPaths
    )
        : this(path, options, fixedResourceId, null, hostPaths) { }

    /// <summary>Creates a model-backed file resource at the supplied path.</summary>
    /// <param name="path">The path of the file resource.</param>
    /// <param name="backupSchema">The model identity used to organize persistent backups by model and version.</param>
    /// <param name="options">The retry and backup settings.</param>
    /// <param name="fixedResourceId">An optional stable physical identity for the resource.</param>
    public FileResource(
        string path,
        StateSchemaMetadata backupSchema,
        FileResourceOptions? options = null,
        ResourceId? fixedResourceId = null
    )
        : this(path, options, fixedResourceId, backupSchema, null) { }

    /// <summary>Creates a model-backed file resource using host-specific default backups.</summary>
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

        if (_options.RevisionVerificationInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "RevisionVerificationInterval must be greater than zero."
            );
        }

        _hostPaths = hostPaths ?? ConfiglueHostPathProfile.Default;
        var backupDirectory = _options.BackupDirectory;
        var previousBackupDirectories = new List<string>();
        var usesPersistentBackupDirectory = false;
        if (backupDirectory is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
            if (string.Equals(backupDirectory, "/", StringComparison.Ordinal))
            {
                _backupDirectory = _directory;
            }
            else if (System.IO.Path.IsPathRooted(backupDirectory))
            {
                _backupDirectory = System.IO.Path.GetFullPath(backupDirectory);
            }
            else
            {
                _backupDirectory = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(_directory, backupDirectory)
                );
            }
            if (
                !System.IO.Path.IsPathRooted(backupDirectory)
                && !string.Equals(backupDirectory, "/", StringComparison.Ordinal)
            )
            {
                previousBackupDirectories.Add(System.IO.Path.GetFullPath(backupDirectory));
            }
        }
        else
        {
            var configuredMode = _options.BackupDirectoryMode;
            var mode = configuredMode.GetValueOrDefault();
            if (configuredMode is null)
            {
                mode =
                    backupSchema is not null
                    || _options.BackupRootDirectory is not null
                    || !string.Equals(
                        _options.BackupDirectoryName,
                        "configlue-backups",
                        StringComparison.Ordinal
                    )
                    || !_options.IncludeModelVersionInBackupDirectory
                        ? FileBackupDirectoryMode.PersistentUserDirectory
                        : FileBackupDirectoryMode.ResourceDirectory;
            }

            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "BackupDirectoryMode is not a valid value."
                );
            }

            if (mode == FileBackupDirectoryMode.ResourceDirectory)
            {
                var resourceBackupName = OperatingSystem.IsWindows() ? "backup" : ".backup";
                _backupDirectory = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(_directory, resourceBackupName)
                );
                previousBackupDirectories.Add(_directory);
            }
            else
            {
                usesPersistentBackupDirectory = true;
                ArgumentException.ThrowIfNullOrWhiteSpace(_options.BackupDirectoryName);
                if (
                    _options.BackupDirectoryName is "." or ".."
                    || _options.BackupDirectoryName.IndexOfAny([
                        '/',
                        '\\',
                        ':',
                        '*',
                        '?',
                        '"',
                        '<',
                        '>',
                        '|',
                        '\0',
                    ]) >= 0
                    || System.IO.Path.IsPathRooted(_options.BackupDirectoryName)
                )
                {
                    throw new ArgumentException(
                        "BackupDirectoryName must be a single directory name.",
                        nameof(options)
                    );
                }

                var backupRoot = _options.BackupRootDirectory;
                if (backupRoot is not null)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
                }

                string fullBackupRoot;
                if (backupRoot is null)
                {
                    fullBackupRoot = ConfiglueStandardPaths.ResolveDirectory(
                        _hostPaths,
                        ConfiglueStandardLocation.BackupRoot
                    );
                }
                else if (System.IO.Path.IsPathRooted(backupRoot))
                {
                    fullBackupRoot = System.IO.Path.GetFullPath(backupRoot);
                }
                else
                {
                    fullBackupRoot = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(_directory, backupRoot)
                    );
                }
                var resolvedBackupDirectory = System.IO.Path.Combine(
                    fullBackupRoot,
                    _options.BackupDirectoryName
                );
                if (_options.IncludeModelVersionInBackupDirectory)
                {
                    if (backupSchema is not { } schema || schema.ModelId is null)
                    {
                        throw new ArgumentException(
                            "A model ID and version are required for model-version backup directories.",
                            nameof(backupSchema)
                        );
                    }

                    var modelVersionDirectory = System.IO.Path.GetFileNameWithoutExtension(
                        StateSchemaReference.GetFileName(schema.ModelId, schema.Version)
                    );
                    resolvedBackupDirectory = System.IO.Path.Combine(
                        resolvedBackupDirectory,
                        modelVersionDirectory
                    );
                }

                _backupDirectory = System.IO.Path.GetFullPath(resolvedBackupDirectory);
                var legacyBackupName = OperatingSystem.IsWindows() ? "backup" : ".backup";
                previousBackupDirectories.Add(
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(_directory, legacyBackupName))
                );
                previousBackupDirectories.Add(_directory);
            }
        }
        _previousBackupDirectories = previousBackupDirectories
            .Where(directory =>
                !string.Equals(
                    directory,
                    _backupDirectory,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal
                )
            )
            .Distinct(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal
            )
            .ToArray();
        _backupFileName = usesPersistentBackupDirectory
            ? _fileName + "." + ConfiglueHashing.GetXxHash3Hex(identityPath)
            : _fileName;

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
        var lockDirectory = _options.LockDirectory;
        if (lockDirectory is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);
        }

        _lockPath = ResolveLockPath(_path, _directory, _fileName, lockDirectory);
    }

    /// <summary>The normalized file path.</summary>
    public string Path => _path;

    /// <summary>The resolved persistent lock sidecar path. Exposed for tests.</summary>
    internal string LockPathForTests => _lockPath;

    /// <summary>Resolves the persistent lock sidecar path for a resource file.</summary>
    internal static string ResolveLockPathForTests(
        string resourcePath,
        string? lockDirectory = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        var fullPath = System.IO.Path.GetFullPath(resourcePath);
        var directory = System.IO.Path.GetDirectoryName(fullPath)!;
        var fileName = System.IO.Path.GetFileName(fullPath);
        return ResolveLockPath(fullPath, directory, fileName, lockDirectory);
    }

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

    /// <inheritdoc />
    public bool AutomaticBackupRecoveryEnabled => _options.AutomaticBackupRecovery;

    private static string GetRevision(ReadOnlySpan<byte> content) =>
        ConfiglueHashing.GetXxHash3Hex(content);
}
