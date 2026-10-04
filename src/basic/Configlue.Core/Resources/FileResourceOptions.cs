namespace Configlue.Resources;

/// <summary>Controls retry, backup, and change-detection behavior for a file-backed resource.</summary>
/// <remarks>Advanced resource option. Polling has no signature fast-path or verification-throttling
/// interval: every <see cref="PollingInterval"/> tick reads the full file content, so polling costs
/// O(file size) I/O per interval. The single-backup contract keeps only the most recently replaced
/// content; multi-generation rotation, automatic recovery, and cross-process lock files are
/// intentionally unsupported — see the reliability contract on the FileResource type.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class FileResourceOptions
{
    /// <summary>Controls how file changes are detected while a caller is waiting.</summary>
    /// <remarks>Hybrid watches the file with filesystem notifications and re-verifies the content revision on every polling tick as a safety net for filesystems that miss events. Polling reads the full content on every tick (content checks only, no lightweight signature fast-path).</remarks>
    public FileChangeDetectionMode ChangeDetectionMode { get; init; } =
        FileChangeDetectionMode.Hybrid;

    /// <summary>The interval between content revision checks while waiting for a change.</summary>
    /// <remarks>Each tick performs a full content read; increase this interval to bound polling I/O on large files.</remarks>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether the previous contents are copied to a single backup file before a successful replacement.</summary>
    /// <remarks>Only the most recently replaced content is retained at the single resolved backup path; concurrent cross-process writers may interleave backup and main-file replacement.</remarks>
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
