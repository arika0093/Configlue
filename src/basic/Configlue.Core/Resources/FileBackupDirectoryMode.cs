namespace Configlue.Resources;

/// <summary>Selects where a file resource stores its backups.</summary>
/// <remarks>Advanced resource option.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public enum FileBackupDirectoryMode
{
    /// <summary>Stores backups under the persistent per-user backup root.</summary>
    PersistentUserDirectory,

    /// <summary>Stores backups beside the resource file using the platform-specific hidden directory.</summary>
    ResourceDirectory,
}
