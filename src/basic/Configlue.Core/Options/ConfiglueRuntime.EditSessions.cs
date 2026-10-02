using Configlue.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    // Contract-test barrier between the post-write snapshot read and its result publication.
    internal Func<CancellationToken, ValueTask>? AfterEditSessionSnapshotResolved { get; set; }

    /// <inheritdoc />
    public ValueTask<EditSession<TModel>> OpenEditSessionAsync(
        CancellationToken cancellationToken = default
    ) =>
        OpenEditSessionCoreAsync(null, cancellationToken, pinnedSubject: null, upstreamState: this);

    /// <inheritdoc />
    public ValueTask<EditSession<TModel>> OpenEditSessionAsync(
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        return OpenEditSessionCoreAsync(
            writePlan,
            cancellationToken,
            pinnedSubject: null,
            upstreamState: this
        );
    }

    private ValueTask<EditSession<TModel>> OpenEditSessionForSubjectAsync(
        IConfiglueSubject subject,
        StateWritePlan? writePlan,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(subject);
        return OpenEditSessionCoreAsync(
            writePlan,
            cancellationToken,
            pinnedSubject: subject,
            upstreamState: new SubjectBoundOptions(this, subject)
        );
    }

    private async ValueTask<EditSession<TModel>> OpenEditSessionCoreAsync(
        StateWritePlan? writePlan,
        CancellationToken cancellationToken,
        IConfiglueSubject? pinnedSubject,
        IReadOnlyState<TModel>? upstreamState
    )
    {
        using var operation = EnterOperation();
        using IDisposable? subjectScope = pinnedSubject is null
            ? null
            : EnterSubject(pinnedSubject);
        var resolvedState = await ResolveCoreAsync(
                null,
                cancellationToken,
                captureContributions: true
            )
            .ConfigureAwait(false);
        var resolved = resolvedState.Result;
        if (resolved.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {resolved.Status}."
            );
        }

        var effectiveWritePlan = _writePlan.OverrideWith(writePlan ?? StateWritePlan.Empty);
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
            ValidateWritePlan(effectiveWritePlan);
        }

        var details = BuildDetailsSnapshot(resolvedState);
        var draft = CloneModel(resolved.Value!);
        var sessionStart = new StateSnapshot<TModel>(CloneModel(resolved.Value!), details);
        var baseline = CloneModel(resolved.Value!);
        var defaultValue = CloneModel(FromFragment(EmptyFragment));
        var expectedRevisions = resolved.Revisions;
        var upstreamGeneration = new UpstreamGenerationCounter();

        async ValueTask<StateCommitResult<TModel>> SaveSessionValueAsync(
            TModel value,
            CancellationToken token
        )
        {
            using IDisposable? saveScope = pinnedSubject is null
                ? null
                : EnterSubject(pinnedSubject);
            var latestState = await ResolveCoreAsync(null, token, captureContributions: true)
                .ConfigureAwait(false);
            var latest = latestState.Result;
            if (latest.Status != StateReadStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Configuration state could not be read before saving: {latest.Status}."
                );
            }

            var hasRevisionChanges = !HaveSameRevisions(expectedRevisions, latest.Revisions);
            var saveBaseline = baseline;
            var saveValue = value;
            var saveContributions = latestState.Contributions;
            var saveRevisions = latest.Revisions;
            if (hasRevisionChanges)
            {
                saveBaseline = latest.Value!;
                saveValue = RebaseConfigurationEdit(baseline, value, saveBaseline);
            }

            var writeResult = await WriteChangesToSourcesAsync(
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
            var committedState = await ResolveCoreAsync(null, token, captureContributions: true)
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
                new StateSnapshot<TModel>(committed.Value, BuildDetailsSnapshot(committedState)),
                committedGeneration
            );
        }

        async ValueTask<StateSnapshot<TModel>> ResolveSessionUpstreamAsync(CancellationToken token)
        {
            using IDisposable? resolveScope = pinnedSubject is null
                ? null
                : EnterSubject(pinnedSubject);
            var latestState = await ResolveCoreAsync(null, token, captureContributions: true)
                .ConfigureAwait(false);
            var latest = latestState.Result;
            if (latest.Status != StateReadStatus.Success || latest.Value is null)
            {
                throw new InvalidOperationException(
                    $"Configuration state could not be read before rebasing: {latest.Status}."
                );
            }

            baseline = latest.Value;
            expectedRevisions = latest.Revisions;
            return new StateSnapshot<TModel>(latest.Value, BuildDetailsSnapshot(latestState));
        }

        return new EditSession<TModel>(
            draft,
            sessionStart,
            SaveSessionValueAsync,
            RebaseConfigurationEdit,
            static (value, baselineValue) => !Diff(baselineValue, value).IsEmpty,
            ResolveSessionUpstreamAsync,
            CloneModel,
            defaultValue,
            upstreamState,
            upstreamGeneration
        );
    }

    private TModel RebaseConfigurationEdit(TModel before, TModel desired, TModel current)
    {
        var changes = Diff(before, desired);
        if (changes.IsEmpty)
        {
            return current;
        }

        var rebase = ConfiglueFragmentRebase.Rebase(
            ModelSchema,
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
            throw LogConflict(
                conflict.Reason
                    ?? $"The configuration edit conflicts with a concurrent change to '{conflict.PathText}'."
            );
        }

        var currentFragment = ToFragment(current);
        if (currentFragment.ApplyChanges((TFragment)rebase.Rebased) is not TFragment updated)
        {
            throw new InvalidOperationException(
                "The rebased edit produced an incompatible fragment."
            );
        }

        return FromFragment(updated);
    }
}
