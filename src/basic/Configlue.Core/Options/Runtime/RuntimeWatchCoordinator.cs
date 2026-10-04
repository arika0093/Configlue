using System.Collections.Concurrent;
using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Owns watch/subscription lifecycle for one runtime: change/reload listeners,
/// the shared watch loop, per-subject watchers, debounce, and watcher shutdown.
///
/// Listener lists are mutated only under the <see cref="RuntimeLifetime"/> gate
/// so registration races with shutdown exactly as before; the watcher-operation
/// table is a concurrent dictionary drained by disposal. Reload reads go through
/// the resolution engine.
/// </summary>
internal sealed class RuntimeWatchCoordinator<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeSourceTopology<TFragment> _topology;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeLifetime _lifetime;
    private readonly RuntimeSubjectContext _subjects;
    private readonly RuntimeModelCloner<TModel, TFragment> _cloner;
    private readonly TimeSpan _onChangeDebounce;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<SubjectWatchSubscription, byte> _watcherOperations =
        new();
    private Func<Task>? _watcherCleanupBarrier;
    private readonly List<Action<TModel>> _changeListeners = [];
    private readonly List<Action<Exception>> _reloadFailureListeners = [];
    private readonly List<Action<StateRevisionVector?>> _reloadListeners = [];
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
        _topology = topology;
        _diagnostics = diagnostics;
        _lifetime = lifetime;
        _subjects = subjects;
        _cloner = cloner;
        _onChangeDebounce = onChangeDebounce;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Internal synchronization hook invoked immediately before a subject watcher removes itself
    /// from tracking and disposes its cancellation source. Tests use it to block the final
    /// lifetime cleanup while asserting that shutdown drains the owned completion.
    /// </summary>
    internal Func<Task>? WatcherCleanupBarrier
    {
        get => Volatile.Read(ref _watcherCleanupBarrier);
        set => Volatile.Write(ref _watcherCleanupBarrier, value);
    }

    /// <summary>Number of subject watchers whose complete lifetime has not yet been drained.</summary>
    internal int WatcherOperationCount => _watcherOperations.Count;

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

    private bool TryTakeSeedBaseline(out StateRevisionVector revisions)
    {
        var seed = Interlocked.Exchange(ref _seedBaseline, null);
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

    internal IDisposable OnChange(Action<TModel> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return _lifetime.Register(() =>
        {
            _changeListeners.Add(listener);
            EnsureWatcherStarted();
            return (IDisposable)new ChangeSubscription(this, listener);
        });
    }

    internal IDisposable OnReload(Action<StateRevisionVector?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return _lifetime.Register(() =>
        {
            _reloadListeners.Add(listener);
            EnsureWatcherStarted();
            return (IDisposable)new ReloadSubscription(this, listener);
        });
    }

    internal IDisposable OnReloadFailed(Action<Exception> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return _lifetime.Register(() =>
        {
            _reloadFailureListeners.Add(listener);
            EnsureWatcherStarted();
            return (IDisposable)new ReloadFailureSubscription(this, listener);
        });
    }

    internal IDisposable WatchSubject(IConfiglueSubject subject, Action<TModel> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return _lifetime.Register(() =>
        {
            var subscription = new SubjectWatchSubscription(this, subject, listener);
            _watcherOperations.TryAdd(subscription, 0);
            subscription.Start();
            return (IDisposable)subscription;
        });
    }

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
            _changeListeners.Clear();
            _reloadFailureListeners.Clear();
            _reloadListeners.Clear();
            _watchCancellation?.Cancel();
            foreach (var operation in _watcherOperations.Keys)
            {
                operation.RequestCancellation();
            }
            watchTask = _watchTask;
            watcherTasks = _watcherOperations
                .Keys.Select(static operation => operation.Completion)
                .ToArray();
        });
        return (watchTask, watcherTasks);
    }

    /// <summary>Releases cancellation resources after shutdown tasks have drained.</summary>
    internal void ReleaseShutdownResources()
    {
        var cancellation = Interlocked.Exchange(ref _watchCancellation, null);
        cancellation?.Dispose();
    }

    private void RemoveChangeListener(Action<TModel> listener)
    {
        _lifetime.Unregister(() =>
        {
            _changeListeners.Remove(listener);
        });
    }

    private void RemoveReloadFailureListener(Action<Exception> listener)
    {
        _lifetime.Unregister(() =>
        {
            _reloadFailureListeners.Remove(listener);
        });
    }

    private void RemoveReloadListener(Action<StateRevisionVector?> listener)
    {
        _lifetime.Unregister(() =>
        {
            _reloadListeners.Remove(listener);
        });
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
                    // is still observed instead of being adopted silently.
                    if (TryTakeSeedBaseline(out var seedRevisions))
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

                await WaitForAnyChangeAsync(waitRevisions, cancellationToken).ConfigureAwait(false);
                if (_onChangeDebounce > TimeSpan.Zero)
                {
                    await DelayForChangeDebounceAsync(cancellationToken).ConfigureAwait(false);
                }

                reloadStarted = true;
                var (current, valueChanged) = await ReadReloadAsync(
                        previousEffective,
                        hasEffective,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (
                    current.Status == StateReadStatus.Success
                    && !RuntimeState.HaveSameRevisions(waitRevisions, current.Revisions)
                )
                {
                    if (valueChanged)
                    {
                        NotifyListeners(current.Value!);
                    }
                    else
                    {
                        NotifyReloaded(current.Revisions);
                    }
                }
                else if (current.Status != StateReadStatus.Success)
                {
                    NotifyReloadFailed(
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
                NotifyReloadFailed(exception);
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

    private async ValueTask<(StateReadResult<TModel> Result, bool ValueChanged)> ReadReloadAsync(
        TModel previousEffective,
        bool hasEffective,
        CancellationToken cancellationToken
    )
    {
        var diagnostic = _diagnostics.Start(ConfiglueDiagnosticEventKind.ReloadStarted);
        try
        {
            var result = await _engine
                .ReadPublicValueAsync(cancellationToken, diagnostic.Id)
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
                    diagnostic.Id,
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

    private Task DelayForChangeDebounceAsync(CancellationToken cancellationToken)
    {
#if NETSTANDARD
        return _timeProvider.Delay(_onChangeDebounce, cancellationToken);
#else
        return Task.Delay(_onChangeDebounce, _timeProvider, cancellationToken);
#endif
    }

    private async ValueTask WaitForSourceChangeAsync(
        StateSource<TFragment> source,
        string? revision,
        CancellationToken cancellationToken
    )
    {
        var diagnostic = _diagnostics.Start(ConfiglueDiagnosticEventKind.WatchStarted, source.Id);
        Exception? failure = null;
        try
        {
            await source
                .WaitForChangeAsync(GetResourceContext(source), revision, cancellationToken)
                .ConfigureAwait(false);
            _diagnostics.Record(
                ConfiglueDiagnosticEventKind.WatchSignaled,
                diagnostic.Id,
                sourceId: source.Id
            );
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (failure is null)
            {
                diagnostic.Complete(ConfiglueDiagnosticEventKind.WatchStopped);
            }
            else
            {
                diagnostic.Fail(
                    ConfiglueDiagnosticEventKind.WatchStopped,
                    failure,
                    failure is OperationCanceledException
                        && cancellationToken.IsCancellationRequested
                );
            }
        }
    }

    private ConfiglueResourceContext GetResourceContext(StateSource<TFragment> source) =>
        _subjects.GetResourceContext(
            source,
            RuntimeModel<TModel, TFragment>.DefaultResourceContext
        );

    private async Task WaitForAnyChangeAsync(
        StateRevisionVector? revisions,
        CancellationToken cancellationToken
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
        if (
            revisions.Revisions.Keys.Any(revisionSourceId =>
                !activeSourceIds.Contains(revisionSourceId)
            )
        )
        {
            return;
        }

        var waitTasks = GetWatchWaitTasks();
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
            // mirroring the fan-out coordination below. Cancellation requested through the
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

    private List<Task> GetWatchWaitTasks()
    {
        var waitTasks = _watchWaitTasks;
        if (waitTasks is null)
        {
            waitTasks = [];
            _watchWaitTasks = waitTasks;
        }
        else
        {
            waitTasks.Clear();
        }

        return waitTasks;
    }

    private void NotifyListeners(TModel value)
    {
        if (!_lifetime.TrySnapshot(_changeListeners, out var listeners))
        {
            return;
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(_cloner.Clone(value));
            }
            catch (Exception exception)
            {
                _diagnostics.Record(
                    ConfiglueDiagnosticEventKind.ObserverFailed,
                    errorCategory: exception.GetType().FullName
                );
            }
        }
    }

    private void NotifyReloaded(StateRevisionVector? revisions)
    {
        if (!_lifetime.TrySnapshot(_reloadListeners, out var listeners))
        {
            return;
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(revisions);
            }
            catch (Exception listenerException)
            {
                _diagnostics.Record(
                    ConfiglueDiagnosticEventKind.ObserverFailed,
                    errorCategory: listenerException.GetType().FullName
                );
            }
        }
    }

    private void NotifyReloadFailed(Exception exception)
    {
        if (!_lifetime.TrySnapshot(_reloadFailureListeners, out var listeners))
        {
            return;
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(exception);
            }
            catch (Exception listenerException)
            {
                _diagnostics.Record(
                    ConfiglueDiagnosticEventKind.ObserverFailed,
                    errorCategory: listenerException.GetType().FullName
                );
            }
        }
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

    internal async Task WatchSubjectChangesAsync(
        SubjectWatchSubscription subscription,
        CancellationToken cancellationToken
    )
    {
        using var scope = _subjects.Enter(subscription.Subject);
        StateReadResult<TModel> previous = default;
        TModel previousEffective = default!;
        var hasEffective = false;
        var hasPrevious = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var reloadStarted = false;
            try
            {
                if (!hasPrevious)
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
                }

                await WaitForSubjectChangeAsync(previous.Revisions, cancellationToken)
                    .ConfigureAwait(false);
                if (_onChangeDebounce > TimeSpan.Zero)
                {
                    await DelayForChangeDebounceAsync(cancellationToken).ConfigureAwait(false);
                }

                reloadStarted = true;
                var (current, valueChanged) = await ReadReloadAsync(
                        previousEffective,
                        hasEffective,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (
                    current.Status == StateReadStatus.Success
                    && !RuntimeState.HaveSameRevisions(previous.Revisions, current.Revisions)
                )
                {
                    if (valueChanged)
                    {
                        try
                        {
                            subscription.Listener(_cloner.Clone(current.Value!));
                        }
                        catch (Exception exception)
                        {
                            _diagnostics.Record(
                                ConfiglueDiagnosticEventKind.ObserverFailed,
                                errorCategory: exception.GetType().FullName
                            );
                        }
                    }
                }
                else if (current.Status != StateReadStatus.Success)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                        .ConfigureAwait(false);
                }

                previous = current;
                if (current.Status == StateReadStatus.Success)
                {
                    previousEffective = current.Value!;
                    hasEffective = true;
                }
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

    private async Task WaitForSubjectChangeAsync(
        StateRevisionVector? revisions,
        CancellationToken cancellationToken
    )
    {
        var activeSources = _topology.GetActiveSources();
        var topologyChanged = _topology.TopologyChangedTask;

        if (revisions is null)
        {
            return;
        }

        var activeSourceIds = _topology.GetActiveSourceIds(activeSources);
        if (
            revisions.Revisions.Keys.Any(revisionSourceId =>
                !activeSourceIds.Contains(revisionSourceId)
            )
        )
        {
            return;
        }

        var waitTasks = new List<Task>();
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
            await waitCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(waitTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The other waits are canceled after the first invalidation.
            }
        }
    }

    private sealed class ChangeSubscription(
        RuntimeWatchCoordinator<TModel, TFragment> owner,
        Action<TModel> listener
    ) : IDisposable
    {
        private RuntimeWatchCoordinator<TModel, TFragment>? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveChangeListener(listener);
    }

    private sealed class ReloadFailureSubscription(
        RuntimeWatchCoordinator<TModel, TFragment> owner,
        Action<Exception> listener
    ) : IDisposable
    {
        private RuntimeWatchCoordinator<TModel, TFragment>? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveReloadFailureListener(listener);
    }

    private sealed class ReloadSubscription(
        RuntimeWatchCoordinator<TModel, TFragment> owner,
        Action<StateRevisionVector?> listener
    ) : IDisposable
    {
        private RuntimeWatchCoordinator<TModel, TFragment>? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveReloadListener(listener);
    }

    internal sealed class SubjectWatchSubscription(
        RuntimeWatchCoordinator<TModel, TFragment> owner,
        IConfiglueSubject subject,
        Action<TModel> listener
    ) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private Task? _task;
        private int _disposed;

        public IConfiglueSubject Subject { get; } = subject;
        public Action<TModel> Listener { get; } = listener;

        // The completion task covers the entire owned lifetime: the watch loop plus the
        // tracking removal and cancellation-source disposal that follow it. The runtime
        // drains this single task during shutdown, so no unowned cleanup continuation can
        // outlive DisposeAsync.
        public Task Completion => Volatile.Read(ref _task) ?? Task.CompletedTask;

        public void Start() => _task = RunLifetimeAsync();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            RequestCancellation();
        }

        public void RequestCancellation()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The watcher already completed and released its cancellation source.
            }
        }

        private async Task RunLifetimeAsync()
        {
            try
            {
                await owner
                    .WatchSubjectChangesAsync(this, _cancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (owner.WatcherCleanupBarrier is { } barrier)
                    {
                        await barrier().ConfigureAwait(false);
                    }
                }
                finally
                {
                    _cancellation.Dispose();
                    owner._watcherOperations.TryRemove(this, out _);
                }
            }
        }
    }
}
