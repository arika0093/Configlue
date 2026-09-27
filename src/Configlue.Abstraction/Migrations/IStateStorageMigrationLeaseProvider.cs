namespace Configlue;

/// <summary>Provides an exclusive lease for one durable storage migration ID.</summary>
/// <remarks>
/// Implement this optional capability on journals that coordinate multiple callers or processes. The lease
/// remains held until its returned disposable is disposed.
/// </remarks>
public interface IStateStorageMigrationLeaseProvider
{
    /// <summary>Acquires an exclusive lease for a migration until the returned disposable is released.</summary>
    ValueTask<IDisposable> AcquireMigrationLeaseAsync(
        string migrationId,
        CancellationToken cancellationToken = default
    );
}
