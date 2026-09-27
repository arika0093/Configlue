namespace Configlue;

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

    /// <summary>An optional directory for backup files. The resource directory is used when unset.</summary>
    public string? BackupDirectory { get; init; }

    /// <summary>How many transient sharing failures are retried.</summary>
    public int RetryCount { get; init; } = 5;

    /// <summary>The delay between transient sharing failures.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// The maximum time to wait for the cross-process sidecar lock before a write fails with an
    /// <see cref="System.IO.IOException"/>. A <see langword="null"/> value (the default) waits until the
    /// operation's cancellation token is signaled. This policy is independent of
    /// <see cref="RetryCount"/> and <see cref="RetryDelay"/>, which only govern transient sharing
    /// failures while replacing a file.
    /// </summary>
    public TimeSpan? LockAcquireTimeout { get; init; }

    /// <summary>The delay between attempts to acquire the cross-process sidecar lock while another process holds it.</summary>
    public TimeSpan LockAcquireRetryDelay { get; init; } = TimeSpan.FromMilliseconds(40);
}
