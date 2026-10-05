namespace Configlue;

/// <summary>Administers source-local writes and source migrations.</summary>
/// <remarks>
/// <para>Advanced application API for explicit per-source operations.</para>
/// <para>
/// Supported storage-migration contract (single-run, configuration-specific primitives only):
/// read an old representation/source, run schema migration, conditionally write the canonical
/// target with revision protection, and optionally verify/retire through application-controlled
/// deployment logic. Each operation verifies its writes by re-reading through the target codec
/// and reports concurrent changes as conflicts.
/// </para>
/// <para>
/// What Core does not provide: durable progress journals, partial multi-target completion state
/// persisted across restarts, restart resume, migration leases, or cross-process exclusion.
/// Concurrent migration runs must be coordinated by the caller. Every operation is idempotent, so
/// a caller resumes by re-running the same migration; targets already holding the requested
/// fragment are skipped after verification. When <c>retireSources</c> is true, selected sources
/// are removed from this state instance only, after all targets verify and only if the effective
/// model remains unchanged. Retirement is never replayed onto other instances.
/// </para>
/// </remarks>
/// <typeparam name="T">The configuration model.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
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
    /// Adopts a legacy representation into its canonical target without registering the legacy
    /// representation as an active runtime source. When the canonical target already holds state,
    /// no write occurs and the result is null. Otherwise the first readable legacy representation
    /// (in enumeration order) is decoded, schema-migrated, written to the canonical target with
    /// revision protection, and re-read for verification. Returns null when no legacy
    /// representation holds migratable state.
    /// </summary>
    ValueTask<StateSourceMigrationResult?> AdoptLegacyAsync(
        SourceId canonicalTargetId,
        IEnumerable<SourceId> legacySourceIds,
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
