namespace Configlue.Resources;

/// <summary>Available change detection strategies for <see cref="FileResource"/>.</summary>
/// <remarks>Advanced resource option.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public enum FileChangeDetectionMode
{
    /// <summary>Use filesystem notifications and polling concurrently.</summary>
    Hybrid,

    /// <summary>Use polling only, without creating a filesystem watcher.</summary>
    Polling,
}
