using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Phase 4+5 of storage migration: retirement baseline capture, pre-retirement
/// verification, source retirement, and post-retirement resolved-value verification.
///
/// Retirement re-reads every target, replays the migration over replacement reads
/// (retired sources read as empty, targets read as migrated), and requires the
/// effective model to be unchanged plus validation to pass before touching the
/// topology. A final target re-read after resolution closes the race between
/// verification and <see cref="RuntimeSourceTopology{TFragment}.RetireSources"/>.
/// </summary>
internal sealed class RuntimeMigrationRetirementStage<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeSourceTopology<TFragment> _topology;
    private readonly RuntimeValidationPipeline<TModel, TFragment> _validation;
    private readonly RuntimeDiagnosticRecorder _diagnostics;

    internal RuntimeMigrationRetirementStage(
        RuntimeResolutionEngine<TModel, TFragment> engine,
        RuntimeSourceTopology<TFragment> topology,
        RuntimeValidationPipeline<TModel, TFragment> validation,
        RuntimeDiagnosticRecorder diagnostics
    )
    {
        _engine = engine;
        _topology = topology;
        _validation = validation;
        _diagnostics = diagnostics;
    }

    /// <summary>Captures the resolved model before migration for the retirement invariant.</summary>
    internal async ValueTask<object> CaptureResolvedBaselineAsync(
        CancellationToken cancellationToken
    )
    {
        var before = await _engine.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        if (before.Result.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read before source retirement: {before.Result.Status}."
            );
        }

        return before.Result.Value!;
    }

    /// <summary>Verifies the retirement invariant, retires sources, and re-verifies targets.</summary>
    internal async ValueTask<SourceId[]> VerifyAndRetireAsync(
        object baselineModel,
        MigrationBaseline<TModel, TFragment> baseline,
        IReadOnlyList<MigrationTargetPlan<TModel, TFragment>> targetPlans,
        IReadOnlyList<StateStorageMigrationTargetResult> targetResults,
        CancellationToken cancellationToken
    )
    {
        var replacements = await BuildRetirementReplacementsAsync(
                baseline,
                targetPlans,
                targetResults,
                cancellationToken
            )
            .ConfigureAwait(false);
        await VerifyResolvedModelUnchangedAsync(baselineModel, replacements, cancellationToken)
            .ConfigureAwait(false);
        await ReverifyTargetRevisionsAsync(targetPlans, targetResults, cancellationToken)
            .ConfigureAwait(false);

        var retiredSourceIds = baseline
            .Contributions.Select(static contribution => contribution.Source.Id)
            .ToArray();
        var active = _topology.RetireSources(retiredSourceIds);
        if (active is not null)
        {
            _diagnostics.SetActiveSources(active.Select(static source => source.Id));
        }

        return retiredSourceIds;
    }

    private async ValueTask<
        Dictionary<SourceId, StateReadResult<TFragment>>
    > BuildRetirementReplacementsAsync(
        MigrationBaseline<TModel, TFragment> baseline,
        IReadOnlyList<MigrationTargetPlan<TModel, TFragment>> targetPlans,
        IReadOnlyList<StateStorageMigrationTargetResult> targetResults,
        CancellationToken cancellationToken
    )
    {
        var currentSchema = RuntimeModel<TModel, TFragment>.Schema.ToMetadata();
        var replacements = new Dictionary<SourceId, StateReadResult<TFragment>>();
        foreach (var contribution in baseline.Contributions)
        {
            replacements.Add(
                contribution.Source.Id,
                StateReadResult<TFragment>
                    .Success(
                        RuntimeModel<TModel, TFragment>.EmptyFragment,
                        contribution.Result.Revision,
                        currentSchema
                    )
                    .FromSource(contribution.Source.Id, contribution.Source.PhysicalOrigin)
            );
        }

        foreach (var plan in targetPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ReadVerifiedTargetForRetirementAsync(
                    plan,
                    targetResults,
                    currentSchema,
                    cancellationToken
                )
                .ConfigureAwait(false);
            replacements.Add(
                plan.Target.Id,
                StateReadResult<TFragment>
                    .Success(plan.Desired, current.Revision, currentSchema)
                    .FromSource(plan.Target.Id, plan.Target.PhysicalOrigin)
            );
        }

        return replacements;
    }

    private async ValueTask<StateReadResult<TFragment>> ReadVerifiedTargetForRetirementAsync(
        MigrationTargetPlan<TModel, TFragment> plan,
        IReadOnlyList<StateStorageMigrationTargetResult> targetResults,
        StateSchemaMetadata currentSchema,
        CancellationToken cancellationToken
    )
    {
        var outcome = targetResults.First(result => result.TargetId == plan.Target.Id);
        var current = (
            await _engine.ReadSourceAsync(plan.Target, cancellationToken).ConfigureAwait(false)
        ).FromSource(plan.Target.Id, plan.Target.PhysicalOrigin);
        if (
            current.Status == StateReadStatus.Unavailable
            || !string.Equals(current.Revision, outcome.TargetRevision, StringComparison.Ordinal)
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"Target source '{plan.Target.Id}' changed before source retirement."
            );
        }

        var currentFragment = current.Status switch
        {
            StateReadStatus.NotFound when plan.Desired.IsEmpty => RuntimeModel<
                TModel,
                TFragment
            >.EmptyFragment,
            StateReadStatus.Success => current.Value
                ?? throw new InvalidOperationException(
                    $"State source '{plan.Target.Id}' returned a null configuration fragment."
                ),
            _ => throw RuntimeState.NewConflict(
                _diagnostics,
                $"Target source '{plan.Target.Id}' is not available for source retirement."
            ),
        };
        if (current.Schema is { } schema)
        {
            currentFragment = await _engine
                .MigrateFragmentAsync(currentFragment, schema, cancellationToken)
                .ConfigureAwait(false);
        }

        if (
            (current.Schema is { } actualSchema && actualSchema != currentSchema)
            || !ConfiglueFragmentComparer.AreEqual(currentFragment, plan.Desired)
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"Target source '{plan.Target.Id}' no longer contains the verified migration result."
            );
        }

        return current;
    }

    private async ValueTask VerifyResolvedModelUnchangedAsync(
        object baselineModel,
        Dictionary<SourceId, StateReadResult<TFragment>> replacements,
        CancellationToken cancellationToken
    )
    {
        // Rewrite replacement values to the verified desired fragments: the reads above
        // already proved target content matches each plan, so resolution must observe
        // exactly the migrated state.
        var resolved = await _engine
            .ResolveAsync(replacements, cancellationToken)
            .ConfigureAwait(false);
        if (
            resolved.Result.Status != StateReadStatus.Success
            || baselineModel is not TModel before
            || !RuntimeModel<TModel, TFragment>.Diff(before, resolved.Result.Value!).IsEmpty
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                "The migrated targets cannot replace the selected sources without changing the effective configuration."
            );
        }

        _validation.Validate(resolved.Result.Value!);
    }

    private async ValueTask ReverifyTargetRevisionsAsync(
        IReadOnlyList<MigrationTargetPlan<TModel, TFragment>> targetPlans,
        IReadOnlyList<StateStorageMigrationTargetResult> targetResults,
        CancellationToken cancellationToken
    )
    {
        var currentSchema = RuntimeModel<TModel, TFragment>.Schema.ToMetadata();
        foreach (var plan in targetPlans)
        {
            var outcome = targetResults.First(result => result.TargetId == plan.Target.Id);
            var latest = (
                await _engine.ReadSourceAsync(plan.Target, cancellationToken).ConfigureAwait(false)
            ).FromSource(plan.Target.Id, plan.Target.PhysicalOrigin);
            if (!string.Equals(latest.Revision, outcome.TargetRevision, StringComparison.Ordinal))
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"Target source '{plan.Target.Id}' changed while source retirement was being verified."
                );
            }

            if (latest.Status == StateReadStatus.NotFound && plan.Desired.IsEmpty)
            {
                continue;
            }

            if (latest.Status != StateReadStatus.Success || latest.Value is null)
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"Target source '{plan.Target.Id}' is not available for source retirement."
                );
            }

            var latestFragment = latest.Schema is { } latestSchema
                ? await _engine
                    .MigrateFragmentAsync(latest.Value, latestSchema, cancellationToken)
                    .ConfigureAwait(false)
                : latest.Value;
            if (
                (latest.Schema is { } actualSchema && actualSchema != currentSchema)
                || !ConfiglueFragmentComparer.AreEqual(latestFragment, plan.Desired)
            )
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"Target source '{plan.Target.Id}' no longer contains the verified migration result."
                );
            }
        }
    }
}
