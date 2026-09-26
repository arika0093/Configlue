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

    /// <summary>An optional directory for backup files. The resource directory is used when unset.</summary>
    public string? BackupDirectory { get; init; }

    /// <summary>How many transient sharing failures are retried.</summary>
    public int RetryCount { get; init; } = 5;

    /// <summary>The delay between transient sharing failures.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(40);
}
