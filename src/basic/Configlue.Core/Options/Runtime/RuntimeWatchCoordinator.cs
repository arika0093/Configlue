using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Thin orchestration over the decomposed watch owners for one runtime: the shared
/// source watch loop, the notification hub, and the per-subject watch manager.
///
/// This type owns only the default/global watch lifecycle (its cancellation, task,
/// wait-task scratch, and read-baseline seed) and ties the collaborators to the
/// <see cref="RuntimeLifetime"/> gate. Source waiting and debounce live in
/// <see cref="RuntimeSourceWatchLoop{TModel, TFragment}"/>, listener storage and
/// failure-isolated dispatch in
/// <see cref="RuntimeWatchNotificationHub{TModel, TFragment}"/>, and keyed subject
/// watcher lifetimes in <see cref="RuntimeSubjectWatchManager{TModel, TFragment}"/>.
///
/// Listener registration and shutdown both run through the lifetime gate so a
/// watcher is either observed by shutdown or rejected because shutdown already
/// began. Reload reads go through the resolution engine.
/// </summary>
internal sealed class RuntimeWatchCoordinator<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeSourceWatchLoop<TModel, TFragment> _watchLoop;
    private readonly RuntimeWatchNotificationHub<TModel, TFragment> _notifications;
    private readonly RuntimeSubjectWatchManager<TModel, TFragment> _subjectsManager;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeLifetime _lifetime;
    private readonly RuntimeSubjectContext _subjects;
    private CancellationTokenSource? _watchCancellation;
    private Task? _watchTask;
    private List<Task>? _watchWaitTasks;

    /// <summary>
    /// Baseline revisions captured from the most recent successful read, with the
    /// ambient subject they were resolved for.
    /// </summary>
    /// <remarks>
    /// The watch loop consumes this once instead of performing its own initial
    /// resolution read. That closes the cold-start lost-wakeup window where an
    /// external change landing between subscription and the loop's first read
    /// would otherwise be adopted as the unobserved baseline and never reported.
    /// Only revisions are retained (never model values), so there is no aliasing
    /// with caller-held snapshots and no additional clone cost on the read path.
    /// </remarks>
    private BaselineSeed? _seedBaseline;

    private sealed record BaselineSeed(StateRevisionVector Revisions, SubjectKey Subject);

    internal RuntimeWatchCoordinator(
        RuntimeResolutionEngine<TModel, TFragment> engine,
        RuntimeSourceTopology<TFragment> topology,
        RuntimeDiagnosticRecorder diagnostics,
        RuntimeLifetime lifetime,
        RuntimeSubjectContext subjects,
        RuntimeModelCloner<TModel, TFragment> cloner,
        TimeSpan onChangeDebounce,
        TimeProvider timeProvider
    )
    {
        _engine = engine;
        _diagnostics = diagnostics;
        _lifetime = lifetime;
        _subjects = subjects;
        _watchLoop = new RuntimeSourceWatchLoop<TModel, TFragment>(
            engine,
            topology,
            diagnostics,
            subjects,
            onChangeDebounce,
            timeProvider
        );
        _notifications = new RuntimeWatchNotificationHub<TModel, TFragment>(
            lifetime,
            diagnostics,
            cloner
        );
        _subjectsManager = new RuntimeSubjectWatchManager<TModel, TFragment>(
            engine,
            _watchLoop,
            subjects,
            diagnostics,
            lifetime,
            cloner
        );
    }

    /// <summary>
    /// Internal synchronization hook invoked immediately before a subject watcher removes itself
    /// from tracking and disposes its cancellation source. Tests use it to block the final
    /// lifetime cleanup while asserting that shutdown drains the owned completion.
    /// </summary>
    internal Func<Task>? WatcherCleanupBarrier
    {
        get => _subjectsManager.WatcherCleanupBarrier;
        set => _subjectsManager.WatcherCleanupBarrier = value;
    }

    /// <summary>Number of subject watchers whose complete lifetime has not yet been drained.</summary>
    internal int WatcherOperationCount => _subjectsManager.WatcherOperationCount;

    /// <summary>
    /// Records the revisions of the most recent successful read as a candidate
    /// baseline for a watch loop that has not performed its initial resolution yet.
    /// </summary>
    /// <remarks>
    /// Consuming the seed is never worse than performing a fresh initial read: the
    /// seed always predates (or coincides with) the loop startup, so waiting on it
    /// detects a superset of the changes a fresh read would observe. A revision
    /// difference without a retained baseline value is treated as a value change;
    /// revision equality still suppresses notification, so a stale seed can only
    /// cause one harmless extra reload cycle, never a missed update or a wrong value.
    /// Seeds are scoped by subject and consumed once.
    /// </remarks>
    internal void NoteReadBaseline(StateReadResult<TModel> result)
    {
        if (result.Status != StateReadStatus.Success || result.Revisions is null)
        {
            return;
        }

        Volatile.Write(ref _seedBaseline, new BaselineSeed(result.Revisions, _subjects.CurrentKey));
    }

    private bool TryPeekSeedBaseline(out StateRevisionVector revisions)
    {
        var seed = Volatile.Read(ref _seedBaseline);
        if (
            seed is not null
            && seed.Subject.Equals(_subjects.CurrentKey)
            && seed.Revisions is not null
        )
        {
            revisions = seed.Revisions;
            return true;
        }

        revisions = null!;
        return false;
    }

    private void ClearSeedBaseline() => Volatile.Write(ref _seedBaseline, null);

    internal IDisposable OnChange(Action<TModel> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return _lifetime.Register(() =>
        {
            var subscription = _notifications.AddChangeListenerCore(listener);
            EnsureWatcherStarted();
            return subscription;
        });
    }

    internal IDisposable OnReload(Action<StateRevisionVector?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return _lifetime.Register(() =>
        {
            var subscription = _notifications.AddReloadListenerCore(listener);
            EnsureWatcherStarted();
            return subscription;
        });
    }

    internal IDisposable OnReloadFailed(Action<Exception> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return _lifetime.Register(() =>
        {
            var subscription = _notifications.AddReloadFailureListenerCore(listener);
            EnsureWatcherStarted();
            return subscription;
        });
    }

    internal IDisposable WatchSubject(IConfiglueSubject subject, Action<TModel> listener) =>
        _subjectsManager.WatchSubject(subject, listener);

    /// <summary>
    /// Captures watch tasks and cancels all watchers. Runs under the lifetime gate
    /// without throwing so repeated disposal stays a no-op.
    /// </summary>
    internal (Task? WatchTask, Task[] WatcherTasks) ShutdownForDispose()
    {
        Task? watchTask = null;
        Task[] watcherTasks = [];
        _lifetime.ExecuteUnderGate(() =>
        {
            _notifications.ClearCore();
            _watchCancellation?.Cancel();
            watchTask = _watchTask;
            watcherTasks = _subjectsManager.CaptureShutdownCore();
        });
        return (watchTask, watcherTasks);
    }

    /// <summary>Releases cancellation resources after shutdown tasks have drained.</summary>
    internal void ReleaseShutdownResources()
    {
        var cancellation = Interlocked.Exchange(ref _watchCancellation, null);
        cancellation?.Dispose();
    }

    private async Task WatchChangesAsync(CancellationToken cancellationToken)
    {
        StateReadResult<TModel> previous = default;
        TModel previousEffective = default!;
        var hasEffective = false;
        var hasPrevious = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var reloadStarted = false;
            try
            {
                StateRevisionVector? waitRevisions;
                if (!hasPrevious)
                {
                    // Prefer a baseline seeded by the most recent read: it predates
                    // loop startup, so an external change racing the first resolution
                    // is still observed instead of being adopted silently. The seed is
                    // only peeked here so a transient first-cycle failure retries with
                    // the same baseline instead of falling back to a post-change read.
                    if (TryPeekSeedBaseline(out var seedRevisions))
                    {
                        waitRevisions = seedRevisions;
                    }
                    else
                    {
                        previous = await _engine
                            .ReadPublicValueAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (previous.Status == StateReadStatus.Success)
                        {
                            previousEffective = previous.Value!;
                            hasEffective = true;
                        }

                        hasPrevious = true;
                        waitRevisions = previous.Revisions;
                    }
                }
                else
                {
                    waitRevisions = previous.Revisions;
                }

                await _watchLoop
                    .WaitForChangeAsync(waitRevisions, cancellationToken, GetWatchWaitTasks())
                    .ConfigureAwait(false);
                await _watchLoop.WaitForDebounceAsync(cancellationToken).ConfigureAwait(false);

                reloadStarted = true;
                var (current, valueChanged) = await _watchLoop
                    .ReadReloadAsync(previousEffective, hasEffective, cancellationToken)
                    .ConfigureAwait(false);
                if (
                    current.Status == StateReadStatus.Success
                    && !RuntimeState.HaveSameRevisions(waitRevisions, current.Revisions)
                )
                {
                    if (valueChanged)
                    {
                        _notifications.NotifyChanged(current.Value!);
                    }
                    else
                    {
                        _notifications.NotifyReloaded(current.Revisions);
                    }
                }
                else if (current.Status != StateReadStatus.Success)
                {
                    _notifications.NotifyReloadFailed(
                        new InvalidOperationException(
                            $"Configuration reload resolved to state status '{current.Status}'."
                        )
                    );
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                        .ConfigureAwait(false);
                }

                previous = current;
                if (current.Status == StateReadStatus.Success)
                {
                    previousEffective = current.Value!;
                    hasEffective = true;
                }

                // The startup seed served its purpose (or was never needed); never
                // reuse a stale baseline on a later cycle. Seeds survive failures
                // above because this line only runs after a completed cycle.
                ClearSeedBaseline();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (!reloadStarted)
                    _diagnostics.Record(
                        ConfiglueDiagnosticEventKind.ReloadFailed,
                        errorCategory: exception.GetType().FullName
                    );
                _notifications.NotifyReloadFailed(exception);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private List<Task> GetWatchWaitTasks()
    {
        var waitTasks = _watchWaitTasks;
        if (waitTasks is null)
        {
            waitTasks = [];
            _watchWaitTasks = waitTasks;
        }

        return waitTasks;
    }

    private void EnsureWatcherStarted()
    {
        if (_watchTask is null || _watchTask.IsCompleted)
        {
            _watchCancellation?.Dispose();
            _watchCancellation = new CancellationTokenSource();
            _watchTask = WatchChangesAsync(_watchCancellation.Token);
        }
    }
}
