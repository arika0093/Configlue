using Configlue.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private async Task WatchSubjectChangesAsync(
        SubjectWatchSubscription subscription,
        CancellationToken cancellationToken
    )
    {
        using var scope = EnterSubject(subscription.Subject);
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
                    previous = await ReadPublicValueAsync(cancellationToken).ConfigureAwait(false);
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
                    && !HaveSameRevisions(previous.Revisions, current.Revisions)
                )
                {
                    if (valueChanged)
                    {
                        try
                        {
                            subscription.Listener(CloneModel(current.Value!));
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
        StateSource<TFragment>[] activeSources;
        Task topologyChanged;
        lock (_sourceGate)
        {
            activeSources = _activeSources;
            topologyChanged = _sourceTopologyChanged.Task;
        }

        if (revisions is null)
        {
            return;
        }

        var activeSourceIds = GetActiveSourceIds(activeSources);
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

    private sealed class SubjectBoundOptions(
        ConfiglueRuntime<TModel, TFragment> owner,
        IConfiglueSubject subject
    )
        : IWritableState<TModel>,
            IConfiglueDetailsRuntime,
            IConfiglueStateSnapshotRuntime<TModel>,
            IConfiglueInspection<TModel>,
            IConfiglueEditSessions<TModel>,
            IConfiglueWritePreview<TModel>
    {
        public IDisposable OnChange(Action<TModel> listener) =>
            owner.WatchSubject(subject, listener);

        public ValueTask<TModel> GetValueAsync(CancellationToken cancellationToken = default) =>
            owner.GetValueForSubjectAsync(subject, cancellationToken);

        public ValueTask<StateWriteReceipt> SaveAsync(
            IConfiglueModelPatch<TModel> patch,
            CancellationToken cancellationToken = default
        ) => owner.SaveForSubjectAsync(subject, patch, cancellationToken);

        public ConfiglueCheckOperation Check(CancellationToken cancellationToken = default) =>
            owner.CreateCheckOperation(subject, cancellationToken);

        public ValueTask<EditSession<TModel>> OpenEditSessionAsync(
            CancellationToken cancellationToken = default
        ) => owner.OpenEditSessionForSubjectAsync(subject, null, cancellationToken);

        public ValueTask<EditSession<TModel>> OpenEditSessionAsync(
            StateWritePlan writePlan,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(writePlan);
            return owner.OpenEditSessionForSubjectAsync(subject, writePlan, cancellationToken);
        }

        public ValueTask<StateWritePreview> PreviewWriteAsync(
            TModel desired,
            CancellationToken cancellationToken = default
        ) => owner.PreviewForSubjectAsync(subject, desired, cancellationToken);

        async ValueTask<ConfiglueDetailsSnapshot> IConfiglueDetailsRuntime.GetDetailsSnapshotAsync(
            CancellationToken cancellationToken
        )
        {
            using var scope = owner.EnterSubject(subject);
            return await ((IConfiglueDetailsRuntime)owner)
                .GetDetailsSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        async ValueTask<
            StateSnapshot<TModel>
        > IConfiglueStateSnapshotRuntime<TModel>.GetSnapshotAsync(
            CancellationToken cancellationToken
        )
        {
            using var scope = owner.EnterSubject(subject);
            return await ((IConfiglueStateSnapshotRuntime<TModel>)owner)
                .GetSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class SubjectWatchSubscription(
        ConfiglueRuntime<TModel, TFragment> owner,
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

    private sealed class SubjectContextScope(
        AsyncLocal<IConfiglueSubject?> context,
        IConfiglueSubject? previous
    ) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                context.Value = previous;
            }
        }
    }
}
