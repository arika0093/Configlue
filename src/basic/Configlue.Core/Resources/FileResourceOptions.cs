namespace Configlue.Resources;

/// <summary>Controls retry and backup behavior for a file-backed resource.</summary>
public sealed class FileResourceOptions
{
    /// <summary>Whether the previous contents are copied to a backup before a successful replacement.</summary>
    public bool CreateBackup { get; init; } = true;

    /// <summary>The suffix appended to a file path for its backup.</summary>
    public string BackupExtension { get; init; } = ".bak";

    /// <summary>Maximum number of generations to retain, including the latest backup. Set to zero to disable backups.</summary>
    public int BackupMaxCount { get; init; } = 1;

    /// <summary>Whether serialized state readers may restore a valid latest backup after missing or corrupt input.</summary>
    /// <remarks>Disabled by default. Corrupt input recovery requires a codec that classifies format errors.</remarks>
    public bool AutomaticBackupRecovery { get; init; }

    /// <summary>An optional exact backup directory. Relative paths use the resource file directory.</summary>
    /// <remarks>When set, this takes precedence over <see cref="BackupDirectoryMode"/> and the backup root settings. The legacy value <c>/</c> selects the resource file directory itself.</remarks>
    public string? BackupDirectory { get; init; }

    /// <summary>Selects the backup location when <see cref="BackupDirectory"/> is not set.</summary>
    /// <remarks>
    /// When unset, model-backed file sources use <see cref="FileBackupDirectoryMode.PersistentUserDirectory"/>,
    /// while standalone resources without model metadata keep the legacy resource-directory location.
    /// </remarks>
    public FileBackupDirectoryMode? BackupDirectoryMode { get; init; }

    /// <summary>An optional root for persistent user backups. Relative paths use the resource file directory.</summary>
    /// <remarks>When unset, the active host profile supplies the persistent backup root.</remarks>
    public string? BackupRootDirectory { get; init; }

    /// <summary>The directory name appended to the persistent backup root.</summary>
    public string BackupDirectoryName { get; init; } = "configlue-backups";

    /// <summary>Whether persistent backups are separated by model ID and schema version.</summary>
    public bool IncludeModelVersionInBackupDirectory { get; init; } = true;

    /// <summary>How many file write failures are retried, excluding cancellation.</summary>
    public int RetryCount { get; init; } = 2;

    /// <summary>The delay between file write attempts.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Calculates the delay before a retry from its one-based attempt number.</summary>
    /// <remarks>When set, this takes precedence over <see cref="RetryDelay"/>.</remarks>
    public Func<int, TimeSpan>? RetryDelayFactory { get; init; }

    /// <summary>
    /// The maximum time to wait for the cross-process sidecar lock before a write fails with an
    /// <see cref="System.IO.IOException"/>. A <see langword="null"/> value (the default) waits until the
    /// operation's cancellation token is signaled. This policy is independent of
    /// <see cref="RetryCount"/> and <see cref="RetryDelay"/>, which govern failures while creating,
    /// writing, flushing, and replacing the file.
    /// </summary>
    public TimeSpan? LockAcquireTimeout { get; init; }

    /// <summary>The delay between attempts to acquire the cross-process sidecar lock while another process holds it.</summary>
    public TimeSpan LockAcquireRetryDelay { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>An optional directory for the persistent cross-process lock sidecar.</summary>
    /// <remarks>When unset, the shared lock directory from <see cref="ConfiglueStandardPaths.GetSharedLockDirectory"/> is used so resource directories stay clean. The legacy value <c>/</c> selects the resource file directory itself.</remarks>
    public string? LockDirectory { get; init; }
}
