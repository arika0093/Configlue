namespace Configlue.Migrations;

/// <summary>Provides an exclusive lease for one durable storage migration ID.</summary>
/// <remarks>
/// Implement this optional capability on journals that coordinate multiple callers or processes. The lease
/// remains held until its returned lease is asynchronously disposed.
/// Advanced application SPI for storage evolution.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IStateStorageMigrationLeaseProvider
{
    /// <summary>Acquires an exclusive lease for a migration until the returned lease is released.</summary>
    ValueTask<IAsyncDisposable> AcquireMigrationLeaseAsync(
        string migrationId,
        CancellationToken cancellationToken = default
    );
}
