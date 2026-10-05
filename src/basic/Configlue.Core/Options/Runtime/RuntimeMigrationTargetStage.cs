using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Phase 3 of storage migration: target projection (pure planning) plus
/// persistence with verification.
///
/// <see cref="BuildTargetPlans"/> never touches I/O: it validates writability and
/// applies projections to the merged fragment. All writes funnel through
/// <see cref="WriteVerifiedAsync"/>, the single conditional-write plus re-read
/// pipeline shared by single-source, legacy, and bulk flows, so provider and
/// representation branches no longer duplicate the verify-and-compare sequence.
/// Same-physical-resource targets (in-place representation replacement) resolve
/// their write base from the already-read source revision instead of a target
/// pre-read that would misclassify old bytes as invalid payload.
/// </summary>
internal sealed class RuntimeMigrationTargetStage<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeWriteCoordinator<TModel, TFragment> _writes;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeMigrationSourceStage<TModel, TFragment> _sources;

    internal RuntimeMigrationTargetStage(
        RuntimeResolutionEngine<TModel, TFragment> engine,
        RuntimeWriteCoordinator<TModel, TFragment> writes,
        RuntimeDiagnosticRecorder diagnostics,
        RuntimeMigrationSourceStage<TModel, TFragment> sources
    )
    {
        _engine = engine;
        _writes = writes;
        _diagnostics = diagnostics;
        _sources = sources;
    }

    /// <summary>Pure planning: validate targets and project the merged fragment.</summary>
    internal List<MigrationTargetPlan<TModel, TFragment>> BuildTargetPlans(
        TFragment merged,
        IReadOnlyDictionary<SourceId, Func<TFragment, TFragment>> targetProjections
    )
    {
        var plans = new List<MigrationTargetPlan<TModel, TFragment>>(targetProjections.Count);
        foreach (var (targetId, project) in targetProjections)
        {
            var target = _sources.FindMigrationSource(targetId);
            _sources.EnsureTargetWritable(target);
            var desired =
                project(merged)
                ?? throw new InvalidOperationException(
                    $"The migration projection for target '{target.Id}' returned null."
                );
            plans.Add(new MigrationTargetPlan<TModel, TFragment>(target, target.Writer!, desired));
        }

        return plans;
    }

    /// <summary>
    /// Reads one target for a conditional write. Same-resource targets cannot be
    /// pre-read through their own codec, so the write base comes from the matching
    /// already-read source contribution instead.
    /// </summary>
    internal async ValueTask<MigrationTargetCurrent<TModel, TFragment>> ReadCurrentAsync(
        StateSource<TFragment> target,
        IReadOnlyList<MigrationSourceContribution<TModel, TFragment>> contributions,
        CancellationToken cancellationToken
    )
    {
        var current = await _engine
            .ReadMigrationSourceAsync(target, cancellationToken)
            .ConfigureAwait(false);
        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException($"Target source '{target.Id}' is unavailable.");
        }

        string? sameResourceBaseRevision = null;
        TFragment currentFragment;
        if (current.Status is StateReadStatus.NotFound or StateReadStatus.Success)
        {
            currentFragment =
                current.Status == StateReadStatus.NotFound
                    ? RuntimeModel<TModel, TFragment>.EmptyFragment
                    : current.Value
                        ?? throw new InvalidOperationException(
                            $"State source '{target.Id}' returned a null configuration fragment."
                        );
        }
        else
        {
            // InvalidPayload on a shared physical resource means "old representation
            // bytes": fall back to the source revision captured through the old codec.
            currentFragment = ResolveSameResourceBase(
                target,
                current.Status,
                contributions,
                out sameResourceBaseRevision
            );
        }

        if (current.Schema is { } targetSchema && current.Status != StateReadStatus.InvalidPayload)
        {
            currentFragment = await _engine
                .MigrateFragmentAsync(currentFragment, targetSchema, cancellationToken)
                .ConfigureAwait(false);
        }

        return new MigrationTargetCurrent<TModel, TFragment>(
            current,
            currentFragment,
            sameResourceBaseRevision ?? current.Revision
        );
    }

    /// <summary>
    /// Resolves the in-place replacement base for a target that shares its physical resource
    /// with a selected migration source but cannot be pre-read through its own codec.
    /// </summary>
    internal TFragment ResolveSameResourceBase(
        StateSource<TFragment> target,
        StateReadStatus targetStatus,
        IReadOnlyList<MigrationSourceContribution<TModel, TFragment>> contributions,
        out string? baseRevision
    )
    {
        var match = contributions.FirstOrDefault(contribution =>
            _sources.SharesPhysicalResource(contribution.Source, target)
        );
        if (match.Source is not null)
        {
            baseRevision = match.Result.Revision;
            return RuntimeModel<TModel, TFragment>.EmptyFragment;
        }

        throw new InvalidOperationException(
            $"Target source '{target.Id}' could not be read: {targetStatus}."
        );
    }

    internal static bool IsAlreadyCurrent(
        MigrationTargetCurrent<TModel, TFragment> current,
        TFragment desired,
        StateSchemaMetadata? currentSchema,
        StateSchemaMetadata currentModelSchema
    )
    {
        var targetIsAlreadyCurrent =
            current.Result.Status == StateReadStatus.Success
            || (current.Result.Status == StateReadStatus.NotFound && desired.IsEmpty);
        return targetIsAlreadyCurrent
            && (currentSchema is null || currentSchema == currentModelSchema)
            && ConfiglueFragmentComparer.AreEqual(current.Fragment, desired);
    }

    /// <summary>Confirms an already-current target did not race; throws on conflict.</summary>
    internal async ValueTask ConfirmAlreadyCurrentAsync(
        StateSource<TFragment> target,
        MigrationTargetCurrent<TModel, TFragment> current,
        TFragment desired,
        StateSchemaMetadata currentModelSchema,
        CancellationToken cancellationToken
    )
    {
        var confirmation = await _engine
            .ReadMigrationSourceAsync(target, cancellationToken)
            .ConfigureAwait(false);
        if (
            confirmation.Status != current.Result.Status
            || !string.Equals(
                confirmation.Revision,
                current.Result.Revision,
                StringComparison.Ordinal
            )
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"Target source '{target.Id}' changed during migration verification."
            );
        }

        if (confirmation.Status != StateReadStatus.Success)
        {
            return;
        }

        var confirmedFragment =
            confirmation.Value
            ?? throw new InvalidOperationException(
                $"State source '{target.Id}' returned a null configuration fragment."
            );
        if (confirmation.Schema is { } confirmationSchema)
        {
            confirmedFragment = await _engine
                .MigrateFragmentAsync(confirmedFragment, confirmationSchema, cancellationToken)
                .ConfigureAwait(false);
        }

        if (
            (confirmation.Schema is { } confirmedSchema && confirmedSchema != currentModelSchema)
            || !ConfiglueFragmentComparer.AreEqual(confirmedFragment, desired)
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"Target source '{target.Id}' changed during migration verification."
            );
        }
    }

    /// <summary>
    /// Writes a migrated fragment with revision protection and verifies it by re-reading
    /// through the target codec. Single shared implementation for every migration flow.
    /// </summary>
    /// <returns>The written target revision.</returns>
    internal async ValueTask<StateWriteResult> WriteVerifiedAsync(
        StateSource<TFragment> target,
        TFragment desiredFragment,
        string? baseRevision,
        CancellationToken cancellationToken
    )
    {
        var write = await _writes
            .WriteObservedAsync(
                target,
                target.Writer!,
                new StateWriteRequest<TFragment>(
                    desiredFragment,
                    Condition: RevisionCondition.FromRevision(baseRevision)
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        var verification = await _engine
            .ReadMigrationSourceAsync(target, cancellationToken)
            .ConfigureAwait(false);
        if (
            verification.Status != StateReadStatus.Success
            || !string.Equals(verification.Revision, write.Revision, StringComparison.Ordinal)
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"Target source '{target.Id}' changed before migration verification completed."
            );
        }

        await VerifyWrittenFragmentAsync(target, verification, desiredFragment, cancellationToken)
            .ConfigureAwait(false);
        return write;
    }

    private async ValueTask VerifyWrittenFragmentAsync(
        StateSource<TFragment> target,
        StateReadResult<TFragment> verification,
        TFragment desiredFragment,
        CancellationToken cancellationToken
    )
    {
        var currentSchema = RuntimeModel<TModel, TFragment>.Schema.ToMetadata();
        var verifiedFragment =
            verification.Value
            ?? throw new InvalidOperationException(
                $"State source '{target.Id}' returned a null configuration fragment after migration."
            );
        if (verification.Schema is { } verificationSchema)
        {
            verifiedFragment = await _engine
                .MigrateFragmentAsync(verifiedFragment, verificationSchema, cancellationToken)
                .ConfigureAwait(false);
        }

        if (
            (verification.Schema is { } actualSchema && actualSchema != currentSchema)
            || !ConfiglueFragmentComparer.AreEqual(verifiedFragment, desiredFragment)
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"Target source '{target.Id}' did not retain the migrated fragment."
            );
        }
    }
}
