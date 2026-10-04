using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Shared source-watch primitive for one runtime: source fan-out waiting,
/// change debounce, and reload reads.
///
/// Both the default/global watch loop and per-subject watch loops wait through
/// this single owner so the wait/debounce logic is defined once. Callers supply
/// the wait-task scratch list: the default loop reuses its cached list (its loop
/// is the only waiter), while subject loops pass a fresh list per wait because
/// many subject loops run concurrently.
/// </summary>
internal sealed class RuntimeSourceWatchLoop<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeSourceTopology<TFragment> _topology;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeSubjectContext _subjects;
    private readonly TimeSpan _debounce;
    private readonly TimeProvider _timeProvider;

    internal RuntimeSourceWatchLoop(
        RuntimeResolutionEngine<TModel, TFragment> engine,
        RuntimeSourceTopology<TFragment> topology,
        RuntimeDiagnosticRecorder diagnostics,
        RuntimeSubjectContext subjects,
        TimeSpan onChangeDebounce,
        TimeProvider timeProvider
    )
    {
        _engine = engine;
        _topology = topology;
        _diagnostics = diagnostics;
        _subjects = subjects;
        _debounce = onChangeDebounce;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Waits until any active source signals a change past <paramref name="revisions"/>
    /// or the topology changes. Returns immediately when there is nothing watchable.
    /// </summary>
    /// <remarks>
    /// Unifies the former default and subject wait paths (the subject path lacked the
    /// single-source fast path). The scratch list is cleared before returning so
    /// callers can reuse it.
    /// </remarks>
    internal async Task WaitForChangeAsync(
        StateRevisionVector? revisions,
        CancellationToken cancellationToken,
        List<Task> scratch
    )
    {
        var activeSources = _topology.GetActiveSources();
        var topologyChanged = _topology.TopologyChangedTask;

        if (revisions is null)
        {
            return;
        }

        if (
            _topology.IsSingleSourceFastPath
            && ReferenceEquals(activeSources, _topology.FastPathSources)
        )
        {
            // Single-file fast path (#231): one watchable source needs no fan-out list or
            // multi-task coordination. Topology retirement replaces the active array, which
            // drops back to the general implementation below.
            await WaitForSingleSourceChangeAsync(
                    activeSources[0],
                    revisions,
                    topologyChanged,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        var activeSourceIds = _topology.GetActiveSourceIds(activeSources);
        if (!revisions.ContainsOnlySources(activeSourceIds))
        {
            return;
        }

        var waitTasks = scratch;
        waitTasks.Clear();
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        try
        {
            foreach (var source in activeSources)
            {
                if (
                    source.Watcher is not null
                    && revisions.TryGetRevision(source.Id, out var revision)
                )
                {
                    waitTasks.Add(
                        WaitForSourceChangeAsync(source, revision, waitCancellation.Token).AsTask()
                    );
                }
            }

            waitTasks.Add(topologyChanged.WaitAsync(waitCancellation.Token));
            var completed = await Task.WhenAny(waitTasks).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await waitCancellation.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    // Drain every source wait even when the winning wait throws or shutdown
                    // cancels it. Otherwise asynchronous watcher cleanup outlives the runtime.
                    await Task.WhenAll(waitTasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
                {
                    // Cancellation of losing waits is expected; the winning exception, if
                    // any, propagates from the try block after all waits finish cleanup.
                }
                finally
                {
                    waitTasks.Clear();
                }
            }
        }
    }

    /// <summary>Waits out the configured change-debounce window, if any.</summary>
    internal Task WaitForDebounceAsync(CancellationToken cancellationToken)
    {
        if (_debounce <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

#if NETSTANDARD
        return _timeProvider.Delay(_debounce, cancellationToken);
#else
        return Task.Delay(_debounce, _timeProvider, cancellationToken);
#endif
    }

    /// <summary>
    /// Reads the current public value for a reload cycle and reports whether the
    /// effective value changed since <paramref name="previousEffective"/>.
    /// </summary>
    internal async ValueTask<(StateReadResult<TModel> Result, bool ValueChanged)> ReadReloadAsync(
        TModel previousEffective,
        bool hasEffective,
        CancellationToken cancellationToken
    )
    {
        var diagnostic = _diagnostics.Start(ConfiglueDiagnosticOperation.Reload);
        try
        {
            var result = await _engine
                .ReadPublicValueAsync(cancellationToken)
                .ConfigureAwait(false);
            var changed =
                result.Status == StateReadStatus.Success
                && (
                    !hasEffective
                    || !RuntimeModel<TModel, TFragment>
                        .Diff(previousEffective, result.Value!)
                        .IsEmpty
                );
            if (changed)
            {
                _diagnostics.Record(
                    ConfiglueDiagnosticEventKind.EffectiveValueChanged,
                    effectiveValueChanged: true
                );
            }
            diagnostic.Complete(
                result.Status == StateReadStatus.Success
                    ? ConfiglueDiagnosticEventKind.ReloadCompleted
                    : ConfiglueDiagnosticEventKind.ReloadFailed,
                result.Status,
                result.Revision is not null,
                changed
            );
            return (result, changed);
        }
        catch (Exception exception)
        {
            diagnostic.Fail(
                ConfiglueDiagnosticEventKind.ReloadFailed,
                exception,
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            );
            throw;
        }
    }

    private async Task WaitForSingleSourceChangeAsync(
        StateSource<TFragment> source,
        StateRevisionVector? revisions,
        Task topologyChanged,
        CancellationToken cancellationToken
    )
    {
        if (
            source.Watcher is null
            || revisions is null
            || !revisions.TryGetRevision(source.Id, out var revision)
        )
        {
            await topologyChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var sourceWait = WaitForSourceChangeAsync(source, revision, waitCancellation.Token)
            .AsTask();
        var topologyWait = topologyChanged.WaitAsync(waitCancellation.Token);
        var completed = await Task.WhenAny(sourceWait, topologyWait).ConfigureAwait(false);
        await waitCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await completed.ConfigureAwait(false);
        }
        finally
        {
            // Drain the loser so asynchronous watcher cleanup cannot outlive the runtime,
            // mirroring the fan-out coordination above. Cancellation requested through the
            // linked source is expected; any winning failure has already propagated.
            var loser = ReferenceEquals(completed, sourceWait) ? topologyWait : sourceWait;
            try
            {
                await loser.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
            {
                // Cancellation of the losing wait is expected.
            }
        }
    }

    private async ValueTask WaitForSourceChangeAsync(
        StateSource<TFragment> source,
        string? revision,
        CancellationToken cancellationToken
    )
    {
        _diagnostics.NoteWatchStarted(source.Id);
        try
        {
            await source
                .WaitForChangeAsync(GetResourceContext(source), revision, cancellationToken)
                .ConfigureAwait(false);
            _diagnostics.NoteWatchSignaled(source.Id);
        }
        finally
        {
            _diagnostics.NoteWatchStopped(source.Id);
        }
    }

    private ConfiglueResourceContext GetResourceContext(StateSource<TFragment> source) =>
        _subjects.GetResourceContext(
            source,
            RuntimeModel<TModel, TFragment>.DefaultResourceContext
        );
}
