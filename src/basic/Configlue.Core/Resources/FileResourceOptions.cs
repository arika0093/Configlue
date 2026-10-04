namespace Configlue.Resources;

/// <summary>Controls retry, backup, and change-detection behavior for a file-backed resource.</summary>
/// <remarks>Advanced resource option.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class FileResourceOptions
{
    /// <summary>Controls how file changes are detected while a caller is waiting.</summary>
    /// <remarks>Hybrid watches the file with filesystem notifications and re-verifies the content revision on every polling tick as a safety net for filesystems that miss events. Polling uses content checks only.</remarks>
    public FileChangeDetectionMode ChangeDetectionMode { get; init; } =
        FileChangeDetectionMode.Hybrid;

    /// <summary>The interval between content revision checks while waiting for a change.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether the previous contents are copied to a single backup file before a successful replacement.</summary>
    public bool CreateBackup { get; init; } = true;

    /// <summary>The suffix appended to the file name for its single backup.</summary>
    public string BackupExtension { get; init; } = ".bak";

    /// <summary>An optional exact backup directory. Relative paths resolve against the resource file directory.</summary>
    /// <remarks>When unset, the single backup sits beside the resource file. The legacy value <c>/</c> also selects the resource file directory.</remarks>
    public string? BackupDirectory { get; init; }

    /// <summary>How many file write failures are retried, excluding cancellation.</summary>
    public int RetryCount { get; init; } = 2;

    /// <summary>The delay between file write attempts.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
}
