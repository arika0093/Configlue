using Configlue.CompilerServices;
using Configlue.Migrations;

namespace Configlue;

/// <summary>
/// Runs declarative, journaled storage migrations.
///
/// Journal/progress handling is decomposed into explicit stages: lease acquisition
/// plus saved-progress load, completed-run re-application (retirement replays on the
/// current instance without reviving old sources), incremental per-target migration
/// with revision-snapshot validation, and retirement finalization. Each stage is a
/// named method so the journal recovery flow reads as orchestration rather than one
/// long loop.
/// </summary>
/// <remarks>Advanced application API for storage evolution.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public static class StateStorageMigrationExtensions
{
    /// <summary>
    /// Migrates the selected source contributions into their targets and persists progress after each verified
    /// target. Journals that implement <see cref="IStateStorageMigrationLeaseProvider"/> serialize runs with
    /// the same migration ID; callers should coordinate concurrent runs when their journal lacks that capability.
    /// </summary>
    public static async ValueTask<StateStorageMigrationProgress> MigrateAsync<TModel, TFragment>(
        this IConfiglueSources<TModel> sources,
        StateStorageMigrationDefinition<TFragment> definition,
        IStateStorageMigrationJournal journal,
        CancellationToken cancellationToken = default
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(journal);
        cancellationToken.ThrowIfCancellationRequested();

        await using var migrationLease = journal
            is IStateStorageMigrationLeaseProvider leaseProvider
            ? await leaseProvider
                .AcquireMigrationLeaseAsync(definition.Id, cancellationToken)
                .ConfigureAwait(false)
            : null;

        var savedProgress = await journal
            .ReadAsync(definition.Id, cancellationToken)
            .ConfigureAwait(false);
        if (savedProgress is not null)
        {
            ValidateProgress(definition, savedProgress);
            if (
                await TryReapplyCompletedRunAsync(
                        sources,
                        definition,
                        savedProgress,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                return savedProgress;
            }
        }

        var completedTargets =
            savedProgress?.CompletedTargetSourceIds.ToHashSet() ?? new HashSet<SourceId>();
        savedProgress = await MigrateRemainingTargetsAsync(
                sources,
                definition,
                journal,
                completedTargets,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await FinalizeRetirementAsync(
                sources,
                definition,
                journal,
                completedTargets,
                savedProgress,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Re-applies a completed journaled run to this state instance. Returns true when
    /// the saved progress was terminal and re-application (including retirement replay)
    /// finished, so the caller can return it directly.
    /// </summary>
    private static async ValueTask<bool> TryReapplyCompletedRunAsync<TModel, TFragment>(
        IConfiglueSources<TModel> sources,
        StateStorageMigrationDefinition<TFragment> definition,
        StateStorageMigrationProgress savedProgress,
        CancellationToken cancellationToken
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        if (!savedProgress.SourcesRetired)
        {
            return false;
        }

        // The journal completed, but retirement only applied to the state
        // instance that ran it. Reapply retirement to this instance so a
        // restart does not revive old sources and hide target changes.
        // MigrateSourcesToTargetsAsync re-verifies target content, source
        // revisions, and the effective-model invariant before retiring,
        // and is idempotent when sources are already retired here.
        if (definition.RetireSources)
        {
            await sources
                .MigrateSourcesToTargetsAsync(
                    definition.SourceIds,
                    BuildProjections(definition.Targets),
                    cancellationToken,
                    retireSources: true
                )
                .ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Migrates every not-yet-completed target one at a time, persisting journal
    /// progress after each verified target so a later run resumes without rework.
    /// </summary>
    private static async ValueTask<StateStorageMigrationProgress?> MigrateRemainingTargetsAsync<
        TModel,
        TFragment
    >(
        IConfiglueSources<TModel> sources,
        StateStorageMigrationDefinition<TFragment> definition,
        IStateStorageMigrationJournal journal,
        HashSet<SourceId> completedTargets,
        CancellationToken cancellationToken
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        Dictionary<SourceId, string?>? sourceRevisionSnapshot = null;
        StateStorageMigrationProgress? savedProgress = null;
        foreach (var target in definition.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Re-migrating an already-completed target is idempotent: the coordinator
            // re-verifies it (WasAlreadyCurrent) instead of rewriting, so recovery
            // also repairs targets changed out of band.
            var result = await sources
                .MigrateSourcesToTargetsAsync(
                    definition.SourceIds,
                    BuildProjections([target]),
                    cancellationToken
                )
                .ConfigureAwait(false);
            sourceRevisionSnapshot = AccumulateSourceSnapshot(
                definition,
                result,
                sourceRevisionSnapshot
            );

            completedTargets.Add(target.TargetSourceId);
            savedProgress = CreateProgress(definition, completedTargets);
            await journal.WriteAsync(savedProgress, cancellationToken).ConfigureAwait(false);
        }

        return savedProgress;
    }

    /// <summary>
    /// Retires sources after all targets are verified and persists the terminal
    /// progress record.
    /// </summary>
    private static async ValueTask<StateStorageMigrationProgress> FinalizeRetirementAsync<
        TModel,
        TFragment
    >(
        IConfiglueSources<TModel> sources,
        StateStorageMigrationDefinition<TFragment> definition,
        IStateStorageMigrationJournal journal,
        HashSet<SourceId> completedTargets,
        StateStorageMigrationProgress? savedProgress,
        CancellationToken cancellationToken
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        if (definition.RetireSources)
        {
            await sources
                .MigrateSourcesToTargetsAsync(
                    definition.SourceIds,
                    BuildProjections(definition.Targets),
                    cancellationToken,
                    retireSources: true
                )
                .ConfigureAwait(false);
            savedProgress = CreateProgress(definition, completedTargets, sourcesRetired: true);
            await journal.WriteAsync(savedProgress, cancellationToken).ConfigureAwait(false);
        }

        return savedProgress ?? CreateProgress(definition, completedTargets);
    }

    private static Dictionary<
        SourceId,
        Func<IConfiglueFragment, IConfiglueFragment>
    > BuildProjections<TFragment>(IEnumerable<StateStorageMigrationTarget<TFragment>> targets)
        where TFragment : class, IConfiglueFragment<TFragment> =>
        targets.ToDictionary(
            static target => target.TargetSourceId,
            static target =>
                (Func<IConfiglueFragment, IConfiglueFragment>)(
                    fragment =>
                        fragment is TFragment typed
                            ? target.Project(typed)
                            : throw new InvalidOperationException(
                                $"Migration source fragment '{fragment.GetType()}' is incompatible with '{typeof(TFragment)}'."
                            )
                )
        );

    /// <summary>
    /// Tracks the first run's source revisions and requires every later target to be
    /// reconciled from the same source snapshot.
    /// </summary>
    private static Dictionary<SourceId, string?> AccumulateSourceSnapshot<TFragment>(
        StateStorageMigrationDefinition<TFragment> definition,
        StateStorageMigrationResult result,
        Dictionary<SourceId, string?>? sourceRevisionSnapshot
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        if (sourceRevisionSnapshot is null)
        {
            sourceRevisionSnapshot = new Dictionary<SourceId, string?>(
                result.SourceRevisions.Revisions.Count
            );
            foreach (var revision in result.SourceRevisions.Revisions)
            {
                sourceRevisionSnapshot.Add(revision.Key, revision.Value);
            }

            return sourceRevisionSnapshot;
        }

        if (
            definition.SourceIds.Any(sourceId =>
                !result.SourceRevisions.TryGetRevision(sourceId, out var revision)
                || !sourceRevisionSnapshot.TryGetValue(sourceId, out var initialRevision)
                || !string.Equals(revision, initialRevision, StringComparison.Ordinal)
            )
        )
        {
            throw new InvalidOperationException(
                "A selected migration source changed between target writes. Retry the migration to reconcile all targets from one source snapshot."
            );
        }

        return sourceRevisionSnapshot;
    }

    private static StateStorageMigrationProgress CreateProgress<TFragment>(
        StateStorageMigrationDefinition<TFragment> definition,
        IEnumerable<SourceId> completedTargets,
        bool sourcesRetired = false
    )
        where TFragment : class, IConfiglueFragment<TFragment> =>
        new(
            definition.Id,
            definition.SourceIds,
            definition.Targets.Select(static target => target.TargetSourceId),
            completedTargets,
            definition.RetireSources,
            sourcesRetired
        );

    private static void ValidateProgress<TFragment>(
        StateStorageMigrationDefinition<TFragment> definition,
        StateStorageMigrationProgress progress
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        if (
            !string.Equals(progress.MigrationId, definition.Id, StringComparison.Ordinal)
            || !progress.SourceIds.SequenceEqual(definition.SourceIds)
            || !progress.TargetSourceIds.SequenceEqual(
                definition.Targets.Select(static target => target.TargetSourceId)
            )
            || progress.RetireSources != definition.RetireSources
        )
        {
            throw new InvalidOperationException(
                $"The stored progress for migration '{definition.Id}' does not match its current definition."
            );
        }
    }
}
