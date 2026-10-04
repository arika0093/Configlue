namespace Configlue.Resources;

/// <summary>Provides opt-in recovery of a resource from a validated backup.</summary>
/// <remarks>Advanced provider SPI.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IResourceBackupRecovery
{
    /// <summary>Whether automatic backup recovery is enabled for this resource.</summary>
    bool AutomaticBackupRecoveryEnabled { get; }

    /// <summary>Attempts to restore the latest backup if it passes the supplied validation.</summary>
    ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
        string? expectedRevision,
        bool expectedMissing,
        Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Recovers a subject-specific resource from a validated backup.</summary>
/// <remarks>Advanced provider SPI.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IContextualResourceBackupRecovery : IResourceBackupRecovery
{
    /// <summary>Recovers a subject-specific resource from a validated backup.</summary>
    ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
        ConfiglueResourceContext context,
        string? expectedRevision,
        bool expectedMissing,
        Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
        CancellationToken cancellationToken = default
    );
}
