using System.Collections.Concurrent;
using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Owns keyed per-subject watch lifetimes for one runtime.
///
/// Each <see cref="SubjectWatchSubscription"/> runs its own watch loop through the
/// shared <see cref="RuntimeSourceWatchLoop{TModel, TFragment}"/> primitive (source
/// wait, debounce, reload read), so wait/debounce behavior is defined once and
/// reused by both the default and subject loops. This manager only adds subject
/// ownership: the ambient subject scope, single-listener delivery, the tracking
/// table drained by shutdown, and the test barrier around final cleanup.
/// </summary>
internal sealed class RuntimeSubjectWatchManager<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeSourceWatchLoop<TModel, TFragment> _watchLoop;
    private readonly RuntimeSubjectContext _subjects;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeLifetime _lifetime;
    private readonly RuntimeModelCloner<TModel, TFragment> _cloner;
    private readonly ConcurrentDictionary<SubjectWatchSubscription, byte> _operations = new();
    private Func<Task>? _watcherCleanupBarrier;

    internal RuntimeSubjectWatchManager(
        RuntimeResolutionEngine<TModel, TFragment> engine,
        RuntimeSourceWatchLoop<TModel, TFragment> watchLoop,
        RuntimeSubjectContext subjects,
        RuntimeDiagnosticRecorder diagnostics,
        RuntimeLifetime lifetime,
        RuntimeModelCloner<TModel, TFragment> cloner
    )
    {
        _engine = engine;
        _watchLoop = watchLoop;
        _subjects = subjects;
        _diagnostics = diagnostics;
        _lifetime = lifetime;
        _cloner = cloner;
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
    internal int WatcherOperationCount => _operations.Count;

    internal IDisposable WatchSubject(IConfiglueSubject subject, Action<TModel> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return _lifetime.Register(() =>
        {
            var subscription = new SubjectWatchSubscription(this, subject, listener);
            _operations.TryAdd(subscription, 0);
            subscription.Start();
            return (IDisposable)subscription;
        });
    }

    /// <summary>
    /// Cancels all subject watchers and captures their completions.
    /// Call under the lifetime gate.
    /// </summary>
    internal Task[] CaptureShutdownCore()
    {
        foreach (var operation in _operations.Keys)
        {
            operation.RequestCancellation();
        }

        return _operations.Keys.Select(static operation => operation.Completion).ToArray();
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

                await _watchLoop
                    .WaitForChangeAsync(previous.Revisions, cancellationToken, [])
                    .ConfigureAwait(false);
                await _watchLoop.WaitForDebounceAsync(cancellationToken).ConfigureAwait(false);

                reloadStarted = true;
                var (current, valueChanged) = await _watchLoop
                    .ReadReloadAsync(previousEffective, hasEffective, cancellationToken)
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

    internal sealed class SubjectWatchSubscription(
        RuntimeSubjectWatchManager<TModel, TFragment> owner,
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
                    owner._operations.TryRemove(this, out _);
                }
            }
        }
    }
}
