using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Owns edit sessions for one runtime: draft creation, rebased saves, and rebase.
///
/// Sessions snapshot the resolved state and its revisions at open time; saves
/// rebase local edits onto the latest upstream state according to the configured
/// conflict resolution, then route through the write coordinator. The post-write
/// snapshot barrier used by contract tests lives here.
/// </summary>
internal sealed class RuntimeEditSessionCoordinator<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeWriteCoordinator<TModel, TFragment> _writes;
    private readonly RuntimeInspectionCoordinator<TModel, TFragment> _inspection;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeLifetime _lifetime;
    private readonly RuntimeSubjectContext _subjects;
    private readonly RuntimeModelCloner<TModel, TFragment> _cloner;
    private readonly WriteConflictResolution _writeConflictResolution;

    internal RuntimeEditSessionCoordinator(
        RuntimeResolutionEngine<TModel, TFragment> engine,
        RuntimeWriteCoordinator<TModel, TFragment> writes,
        RuntimeInspectionCoordinator<TModel, TFragment> inspection,
        RuntimeDiagnosticRecorder diagnostics,
        RuntimeLifetime lifetime,
        RuntimeSubjectContext subjects,
        RuntimeModelCloner<TModel, TFragment> cloner,
        WriteConflictResolution writeConflictResolution
    )
    {
        _engine = engine;
        _writes = writes;
        _inspection = inspection;
        _diagnostics = diagnostics;
        _lifetime = lifetime;
        _subjects = subjects;
        _cloner = cloner;
        _writeConflictResolution = writeConflictResolution;
    }

    // Contract-test barrier between the post-write snapshot read and its result publication.
    internal Func<CancellationToken, ValueTask>? AfterEditSessionSnapshotResolved { get; set; }

    internal async ValueTask<EditSession<TModel>> OpenEditSessionCoreAsync(
        StateWritePlan? writePlan,
        CancellationToken cancellationToken,
        IConfiglueSubject? pinnedSubject,
        IReadOnlyState<TModel>? upstreamState
    )
    {
        using var operation = _lifetime.EnterOperation();
        using IDisposable? subjectScope = pinnedSubject is null
            ? null
            : _subjects.Enter(pinnedSubject);
        var resolvedState = await _engine
            .ResolveAsync(null, cancellationToken, captureContributions: true)
            .ConfigureAwait(false);
        var resolved = resolvedState.Result;
        if (resolved.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {resolved.Status}."
            );
        }

        var effectiveWritePlan = _writes.Plan.OverrideWith(writePlan ?? StateWritePlan.Empty);
        if (
            effectiveWritePlan.DefaultSourceId is null
            && effectiveWritePlan.PropertyRoutes.Count == 0
        )
        {
            throw new InvalidOperationException(
                "No writable state source is registered for this model."
            );
        }

        if (effectiveWritePlan.PropertyRoutes.Count > 0)
        {
            _writes.ValidateWritePlan(effectiveWritePlan);
        }

        var details = _inspection.BuildDetailsSnapshot(resolvedState);
        var draft = _cloner.Clone(resolved.Value!);
        var sessionStart = new StateSnapshot<TModel>(_cloner.Clone(resolved.Value!), details);
        var baseline = _cloner.Clone(resolved.Value!);
        var defaultValue = _cloner.Clone(
            RuntimeModel<TModel, TFragment>.FromFragment(
                RuntimeModel<TModel, TFragment>.EmptyFragment
            )
        );
        var expectedRevisions = resolved.Revisions;
        var upstreamGeneration = new UpstreamGenerationCounter();

        async ValueTask<StateCommitResult<TModel>> SaveSessionValueAsync(
            TModel value,
            CancellationToken token
        )
        {
            using IDisposable? saveScope = pinnedSubject is null
                ? null
                : _subjects.Enter(pinnedSubject);
            var latestState = await _engine
                .ResolveAsync(null, token, captureContributions: true)
                .ConfigureAwait(false);
            var latest = latestState.Result;
            if (latest.Status != StateReadStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Configuration state could not be read before saving: {latest.Status}."
                );
            }

            var hasRevisionChanges = !RuntimeState.HaveSameRevisions(
                expectedRevisions,
                latest.Revisions
            );
            var saveBaseline = baseline;
            var saveValue = value;
            var saveContributions = latestState.Contributions;
            var saveRevisions = latest.Revisions;
            if (hasRevisionChanges)
            {
                saveBaseline = latest.Value!;
                saveValue = RebaseConfigurationEdit(baseline, value, saveBaseline);
            }

            var writeResult = await _writes
                .WriteChangesToSourcesAsync(
                    saveBaseline,
                    saveValue,
                    saveRevisions,
                    saveContributions,
                    effectiveWritePlan,
                    token
                )
                .ConfigureAwait(false);

            // The effective state after the write can differ from the pre-write rebase target
            // when higher-priority or merged contributions participate, and the receipt only
            // carries source revisions. Resolve once more so the session baseline, the closure
            // baseline, and the expected revisions all describe the exact committed state.
            // A notification observed during or after this read may describe a newer state.
            // Only generations known before the read can safely be represented by its result.
            var committedGeneration = upstreamGeneration.Capture();
            var committedState = await _engine
                .ResolveAsync(null, token, captureContributions: true)
                .ConfigureAwait(false);
            var committed = committedState.Result;
            if (committed.Status != StateReadStatus.Success || committed.Value is null)
            {
                throw new InvalidOperationException(
                    $"Configuration state could not be read after saving: {committed.Status}."
                );
            }

            if (AfterEditSessionSnapshotResolved is { } afterSnapshotResolved)
            {
                await afterSnapshotResolved(token).ConfigureAwait(false);
            }

            baseline = committed.Value;
            expectedRevisions = committed.Revisions;
            return new StateCommitResult<TModel>(
                writeResult,
                new StateSnapshot<TModel>(
                    committed.Value,
                    _inspection.BuildDetailsSnapshot(committedState)
                ),
                committedGeneration
            );
        }

        async ValueTask<SessionUpstreamResolution<TModel>> ResolveSessionUpstreamAsync(
            CancellationToken token
        )
        {
            using IDisposable? resolveScope = pinnedSubject is null
                ? null
                : _subjects.Enter(pinnedSubject);
            var latestState = await _engine
                .ResolveAsync(null, token, captureContributions: true)
                .ConfigureAwait(false);
            var latest = latestState.Result;
            if (latest.Status != StateReadStatus.Success || latest.Value is null)
            {
                throw new InvalidOperationException(
                    $"Configuration state could not be read before rebasing: {latest.Status}."
                );
            }

            // Read-only: the saved baseline/expectedRevisions advance only after the
            // session successfully rebases onto this snapshot (see ApplySessionRebase).
            return new SessionUpstreamResolution<TModel>(
                new StateSnapshot<TModel>(
                    latest.Value,
                    _inspection.BuildDetailsSnapshot(latestState)
                ),
                latest.Revisions
            );
        }

        void ApplySessionRebase(SessionUpstreamResolution<TModel> resolved)
        {
            baseline = resolved.Snapshot.Value;
            expectedRevisions = resolved.Revisions;
        }

        return new EditSession<TModel>(
            draft,
            sessionStart,
            SaveSessionValueAsync,
            RebaseConfigurationEdit,
            static (value, baselineValue) =>
                !RuntimeModel<TModel, TFragment>.Diff(baselineValue, value).IsEmpty,
            ResolveSessionUpstreamAsync,
            ApplySessionRebase,
            _cloner.Clone,
            defaultValue,
            upstreamState,
            upstreamGeneration
        );
    }

    private TModel RebaseConfigurationEdit(TModel before, TModel desired, TModel current)
    {
        var changes = RuntimeModel<TModel, TFragment>.Diff(before, desired);
        if (changes.IsEmpty)
        {
            return current;
        }

        var rebase = ConfiglueFragmentRebase.Rebase(
            RuntimeModel<TModel, TFragment>.Schema,
            changes,
            before,
            desired,
            current,
            localWinsCollections: _writeConflictResolution == WriteConflictResolution.LastWriteWins
        );
        if (
            rebase.HasConflicts
            && _writeConflictResolution == WriteConflictResolution.FailOnConflict
        )
        {
            var conflict = rebase.Conflicts[0];
            throw RuntimeState.NewConflict(
                _diagnostics,
                conflict.Reason
                    ?? $"The configuration edit conflicts with a concurrent change to '{conflict.PathText}'."
            );
        }

        var currentFragment = RuntimeModel<TModel, TFragment>.ToFragment(current);
        if (currentFragment.ApplyChanges((TFragment)rebase.Rebased) is not TFragment updated)
        {
            throw new InvalidOperationException(
                "The rebased edit produced an incompatible fragment."
            );
        }

        return RuntimeModel<TModel, TFragment>.FromFragment(updated);
    }
}
