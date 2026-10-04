using Configlue.CompilerServices;

namespace Configlue;

/// <summary>Runs declarative, journaled storage migrations.</summary>
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
            if (savedProgress.SourcesRetired)
            {
                // The journal completed, but retirement only applied to the state
                // instance that ran it. Reapply retirement to this instance so a
                // restart does not revive old sources and hide target changes.
                // MigrateSourcesToTargetsAsync re-verifies target content, source
                // revisions, and the effective-model invariant before retiring,
                // and is idempotent when sources are already retired here.
                if (definition.RetireSources)
                {
                    var reapplyProjections = definition.Targets.ToDictionary(
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
                    await sources
                        .MigrateSourcesToTargetsAsync(
                            definition.SourceIds,
                            reapplyProjections,
                            cancellationToken,
                            retireSources: true
                        )
                        .ConfigureAwait(false);
                }

                return savedProgress;
            }
        }

        var completedTargets =
            savedProgress?.CompletedTargetSourceIds.ToHashSet() ?? new HashSet<SourceId>();
        Dictionary<SourceId, string?>? sourceRevisionSnapshot = null;
        foreach (var target in definition.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projections = new Dictionary<SourceId, Func<IConfiglueFragment, IConfiglueFragment>>
            {
                [target.TargetSourceId] = fragment =>
                    fragment is TFragment typed
                        ? target.Project(typed)
                        : throw new InvalidOperationException(
                            $"Migration source fragment '{fragment.GetType()}' is incompatible with '{typeof(TFragment)}'."
                        ),
            };
            var result = await sources
                .MigrateSourcesToTargetsAsync(definition.SourceIds, projections, cancellationToken)
                .ConfigureAwait(false);
            if (sourceRevisionSnapshot is null)
            {
                sourceRevisionSnapshot = new Dictionary<SourceId, string?>(
                    result.SourceRevisions.Revisions.Count
                );
                foreach (var revision in result.SourceRevisions.Revisions)
                {
                    sourceRevisionSnapshot.Add(revision.Key, revision.Value);
                }
            }
            else if (
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

            completedTargets.Add(target.TargetSourceId);
            savedProgress = CreateProgress(definition, completedTargets);
            await journal.WriteAsync(savedProgress, cancellationToken).ConfigureAwait(false);
        }

        if (definition.RetireSources)
        {
            var allProjections = definition.Targets.ToDictionary(
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
            await sources
                .MigrateSourcesToTargetsAsync(
                    definition.SourceIds,
                    allProjections,
                    cancellationToken,
                    retireSources: true
                )
                .ConfigureAwait(false);
            savedProgress = CreateProgress(definition, completedTargets, sourcesRetired: true);
            await journal.WriteAsync(savedProgress, cancellationToken).ConfigureAwait(false);
        }

        return savedProgress ?? CreateProgress(definition, completedTargets);
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
