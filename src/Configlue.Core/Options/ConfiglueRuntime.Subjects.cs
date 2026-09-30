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
        var hasPrevious = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!hasPrevious)
                {
                    previous = await ReadPublicValueAsync(cancellationToken).ConfigureAwait(false);
                    hasPrevious = true;
                }

                await WaitForSubjectChangeAsync(previous.Revisions, cancellationToken)
                    .ConfigureAwait(false);
                if (_onChangeDebounce > TimeSpan.Zero)
                {
                    await Task.Delay(_onChangeDebounce, cancellationToken).ConfigureAwait(false);
                }

                var current = await ReadPublicValueAsync(cancellationToken).ConfigureAwait(false);
                if (
                    current.Status == StateReadStatus.Success
                    && !HaveSameRevisions(previous.Revisions, current.Revisions)
                )
                {
                    try
                    {
                        subscription.Listener(CloneModel(current.Value!));
                    }
                    catch (Exception exception)
                    {
                        _logger?.LogError(
                            ListenerFailureEvent,
                            exception,
                            "A change listener failed for {ModelType} state {StateName} and subject {SubjectKey}.",
                            typeof(TModel).FullName,
                            _stateName,
                            subscription.Subject.Key.Value
                        );
                    }
                }
                else if (current.Status != StateReadStatus.Success)
                {
                    _logger?.LogWarning(
                        WatchFailureEvent,
                        "A configuration reload resolved to {ReadStatus} for {ModelType} state {StateName} and subject {SubjectKey}.",
                        current.Status,
                        typeof(TModel).FullName,
                        _stateName,
                        subscription.Subject.Key.Value
                    );
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                        .ConfigureAwait(false);
                }

                previous = current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger?.LogError(
                    WatchFailureEvent,
                    exception,
                    "Watching configuration changes failed for {ModelType} state {StateName} and subject {SubjectKey}.",
                    typeof(TModel).FullName,
                    _stateName,
                    subscription.Subject.Key.Value
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
        if (revisions is null)
        {
            return;
        }

        var waitTasks = new List<Task>();
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        try
        {
            foreach (var source in GetActiveSources())
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

            if (waitTasks.Count == 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return;
            }

            await Task.WhenAny(waitTasks).ConfigureAwait(false);
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
    ) : IWritableState<TModel>, IConfiglueDetailsRuntime
    {
        public IDisposable OnChange(Action<TModel> listener) =>
            owner.WatchSubject(subject, listener);

        public ValueTask<TModel> GetValueAsync(CancellationToken cancellationToken = default) =>
            owner.GetValueForSubjectAsync(subject, cancellationToken);

        public ValueTask<StateWriteReceipt> SaveAsync(
            IConfigluePatch patch,
            CancellationToken cancellationToken = default
        ) => owner.SaveForSubjectAsync(subject, patch, cancellationToken);

        async ValueTask<ConfiglueDetailsSnapshot> IConfiglueDetailsRuntime.GetDetailsSnapshotAsync(
            CancellationToken cancellationToken
        )
        {
            using var scope = owner.EnterSubject(subject);
            return await ((IConfiglueDetailsRuntime)owner)
                .GetDetailsSnapshotAsync(cancellationToken)
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
        public Task Completion => Volatile.Read(ref _task) ?? Task.CompletedTask;

        public void Start() => _task = owner.WatchSubjectChangesAsync(this, _cancellation.Token);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellation.Cancel();
            owner._subjectSubscriptions.TryRemove(this, out _);
            _ = DisposeCancellationAfterCompletionAsync(Completion);
        }

        private async Task DisposeCancellationAfterCompletionAsync(Task completion)
        {
            try
            {
                await completion.ConfigureAwait(false);
            }
            finally
            {
                _cancellation.Dispose();
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
