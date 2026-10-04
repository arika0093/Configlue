namespace Configlue.Migrations;

/// <summary>Persists migration progress so an application can resume after a process restart.</summary>
/// <remarks>Advanced application SPI for storage evolution.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IStateStorageMigrationJournal
{
    /// <summary>Loads the last durable progress snapshot for a migration ID.</summary>
    ValueTask<StateStorageMigrationProgress?> ReadAsync(
        string migrationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Atomically replaces the durable progress snapshot for a migration.</summary>
    ValueTask WriteAsync(
        StateStorageMigrationProgress progress,
        CancellationToken cancellationToken = default
    );
}
