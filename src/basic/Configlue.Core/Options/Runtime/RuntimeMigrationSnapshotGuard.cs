using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Mutable baseline of one bulk migration: selected contributions plus the revision
/// vector reported to journal callers. The snapshot guard refreshes both in place
/// when a re-read proves the fragment unchanged; anything else is a conflict.
/// </summary>
internal sealed class MigrationBaseline<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    internal MigrationBaseline(
        List<MigrationSourceContribution<TModel, TFragment>> contributions,
        List<StateRevision> revisions
    )
    {
        Contributions = contributions;
        Revisions = revisions;
    }

    internal List<MigrationSourceContribution<TModel, TFragment>> Contributions { get; }

    internal List<StateRevision> Revisions { get; }
}

/// <summary>
/// Source revision/conflict validation running before every destructive action.
///
/// Re-reads each selected source and compares status, schema, and revision first.
/// A revision-only drift triggers a fragment re-read: identical content refreshes the
/// baseline, changed content (or lost availability) becomes a conflict. Keeps the
/// "validate before destroy" rule in one place instead of interleaved with writes.
/// </summary>
internal sealed class RuntimeMigrationSnapshotGuard<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeDiagnosticRecorder _diagnostics;

    internal RuntimeMigrationSnapshotGuard(
        RuntimeResolutionEngine<TModel, TFragment> engine,
        RuntimeDiagnosticRecorder diagnostics
    )
    {
        _engine = engine;
        _diagnostics = diagnostics;
    }

    internal async ValueTask VerifyUnchangedAsync(
        MigrationBaseline<TModel, TFragment> baseline,
        CancellationToken cancellationToken
    )
    {
        var contributions = baseline.Contributions;
        for (var index = 0; index < contributions.Count; index++)
        {
            var contribution = contributions[index];
            var latest = await _engine
                .ReadMigrationSourceAsync(contribution.Source, cancellationToken)
                .ConfigureAwait(false);
            if (
                latest.Status == contribution.Result.Status
                && latest.Schema == contribution.Result.Schema
                && string.Equals(
                    latest.Revision,
                    contribution.Result.Revision,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }

            if (
                latest.Status != contribution.Result.Status
                || latest.Schema != contribution.Result.Schema
            )
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"Source '{contribution.Source.Id}' changed while the storage migration was running."
                );
            }

            var latestFragment = await ReadGuardedFragmentAsync(
                    contribution,
                    latest,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!ConfiglueFragmentComparer.AreEqual(latestFragment, contribution.Fragment))
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"Source '{contribution.Source.Id}' changed while the storage migration was running."
                );
            }

            contributions[index] = contribution with { Result = latest, Fragment = latestFragment };
            baseline.Revisions[index] = new StateRevision(contribution.Source.Id, latest.Revision);
        }
    }

    private async ValueTask<TFragment> ReadGuardedFragmentAsync(
        MigrationSourceContribution<TModel, TFragment> contribution,
        StateReadResult<TFragment> latest,
        CancellationToken cancellationToken
    )
    {
        var latestFragment = latest.Status switch
        {
            StateReadStatus.NotFound => RuntimeModel<TModel, TFragment>.EmptyFragment,
            StateReadStatus.Success => latest.Value
                ?? throw new InvalidOperationException(
                    $"State source '{contribution.Source.Id}' returned a null configuration fragment."
                ),
            _ => throw RuntimeState.NewConflict(
                _diagnostics,
                $"Source '{contribution.Source.Id}' became unavailable during migration."
            ),
        };
        if (latest.Schema is { } latestSchema)
        {
            latestFragment = await _engine
                .MigrateFragmentAsync(latestFragment, latestSchema, cancellationToken)
                .ConfigureAwait(false);
        }

        return latestFragment;
    }
}
