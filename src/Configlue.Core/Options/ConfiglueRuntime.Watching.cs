using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private List<Task>? _watchWaitTasks;
    private ActiveSourceIdSet? _activeSourceIdSet;

    private async Task WatchChangesAsync(CancellationToken cancellationToken)
    {
        StateReadResult<TModel> previous = default;
        var hasPrevious = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!hasPrevious)
                {
                    previous = await ReadAsync(cancellationToken).ConfigureAwait(false);
                    hasPrevious = true;
                }

                await WaitForAnyChangeAsync(previous.Revisions, cancellationToken)
                    .ConfigureAwait(false);
                if (_onChangeDebounce > TimeSpan.Zero)
                {
                    await Task.Delay(_onChangeDebounce, cancellationToken).ConfigureAwait(false);
                }

                var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
                if (
                    current.Status == StateReadStatus.Success
                    && !HaveSameRevisions(previous.Revisions, current.Revisions)
                )
                {
                    _logger?.LogDebug(
                        WatchReloadEvent,
                        "Configuration changed for {ModelType} state {StateName}; observed sources {SourceIds}.",
                        typeof(TModel).FullName,
                        _stateName,
                        current.Revisions is { } revisions
                            ? string.Join(",", revisions.Revisions.Keys)
                            : string.Empty
                    );
                    NotifyListeners(current.Value!);
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
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                var watcherSources = GetActiveSources()
                    .Where(static source => source.Watcher is not null)
                    .ToArray();
                _logger?.LogError(
                    WatchFailureEvent,
                    exception,
                    "Watching configuration changes failed for {ModelType} state {StateName}; sources {SourceIds}, resources {ResourceIds}.",
                    typeof(TModel).FullName,
                    _stateName,
                    string.Join(",", watcherSources.Select(static source => source.Id)),
                    string.Join(
                        ",",
                        watcherSources
                            .Where(static source => source.ResourceId is not null)
                            .Select(static source => source.ResourceId!.Value.Value)
                    )
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

    private async Task WaitForAnyChangeAsync(
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
            await waitCancellation.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(waitTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The remaining source waits are canceled after the first source reports a change.
        }
        finally
        {
            waitTasks.Clear();
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

    private HashSet<string> GetActiveSourceIds(StateSource<TFragment>[] activeSources)
    {
        var cached = Volatile.Read(ref _activeSourceIdSet);
        if (cached is not null && ReferenceEquals(cached.Sources, activeSources))
        {
            return cached.Ids;
        }

        var ids = new HashSet<string>(activeSources.Length, StringComparer.Ordinal);
        foreach (var source in activeSources)
        {
            ids.Add(source.Id);
        }

        Volatile.Write(ref _activeSourceIdSet, new ActiveSourceIdSet(activeSources, ids));
        return ids;
    }

    private sealed class ActiveSourceIdSet
    {
        public ActiveSourceIdSet(StateSource<TFragment>[] sources, HashSet<string> ids)
        {
            Sources = sources;
            Ids = ids;
        }

        public StateSource<TFragment>[] Sources { get; }

        public HashSet<string> Ids { get; }
    }

    private void NotifyListeners(TModel value)
    {
        Action<TModel>[] listeners;
        lock (_changeGate)
        {
            if (_disposed)
            {
                return;
            }

            listeners = _changeListeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(CloneModel(value));
            }
            catch (Exception exception)
            {
                _logger?.LogError(
                    ListenerFailureEvent,
                    exception,
                    "A configuration change listener failed for {ModelType} state {StateName}.",
                    typeof(TModel).FullName,
                    _stateName
                );
            }
        }
    }

    private void NotifyReloadFailed(Exception exception)
    {
        Action<Exception>[] listeners;
        lock (_changeGate)
        {
            if (_disposed)
            {
                return;
            }

            listeners = _reloadFailureListeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(exception);
            }
            catch (Exception listenerException)
            {
                _logger?.LogError(
                    ReloadFailureListenerEvent,
                    listenerException,
                    "A reload-failure listener failed for {ModelType} state {StateName}.",
                    typeof(TModel).FullName,
                    _stateName
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
}
