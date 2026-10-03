namespace Configlue;

/// <summary>Administers source-local writes and source migrations.</summary>
/// <typeparam name="T">The configuration model.</typeparam>
public interface IConfiglueSources<T>
{
    /// <summary>
    /// Migrates one source's contribution into another writable source without merging unrelated sources.
    /// The written fragment is re-read through the target and verified, so a target that did not retain
    /// the migrated fragment (or reports a stale schema) is reported as a conflict.
    /// </summary>
    ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        SourceId sourceId,
        SourceId targetId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Migrates one source contribution using typed logical source keys.</summary>
    ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        SourceKey<T> sourceKey,
        SourceKey<T> targetKey,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Migrates selected source contributions into projected targets. Implementations verify each write and
    /// skip targets already holding the requested fragment, so a partially completed operation can be retried.
    /// When <paramref name="retireSources"/> is true, selected sources are removed from this state instance
    /// after all targets verify and only if the effective model remains unchanged.
    /// </summary>
    ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<SourceId> sourceIds,
        IReadOnlyDictionary<
            SourceId,
            Func<IConfiglueFragment, IConfiglueFragment>
        > targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    );

    /// <summary>Migrates source contributions using typed logical source keys.</summary>
    ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<SourceKey<T>> sourceKeys,
        IReadOnlyDictionary<
            SourceKey<T>,
            Func<IConfiglueFragment, IConfiglueFragment>
        > targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    );

    /// <summary>Applies explicit source-local patches and groups compatible writes by physical resource identity.</summary>
    ValueTask<StateWriteReceipt> ApplyPatchesAsync(
        IEnumerable<StateSourcePatch> patches,
        CancellationToken cancellationToken = default
    );
}
