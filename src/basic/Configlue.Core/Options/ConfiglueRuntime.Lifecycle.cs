using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Configlue.CompilerServices;
using Configlue.Sources;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <inheritdoc />
    public void Dispose()
    {
        lock (_changeGate)
        {
            DisposeCoreLocked();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? watchTask;
        Task[] watcherTasks;
        Task operationsDrained;
        lock (_changeGate)
        {
            // Registration and shutdown share this gate: a watcher is either rejected because
            // shutdown already began or observed here before shutdown releases the gate.
            DisposeCoreLocked();
            watchTask = _watchTask;
            watcherTasks = _watcherOperations
                .Keys.Select(static operation => operation.Completion)
                .ToArray();
            operationsDrained = CaptureOperationsDrainedLocked();
        }

        List<Exception>? errors = null;
        if (watchTask is not null)
        {
            try
            {
                await watchTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
        }
        foreach (var watcherTask in watcherTasks)
        {
            try
            {
                await watcherTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
        }
        try
        {
            await operationsDrained.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (errors ??= []).Add(exception);
        }
        _watchCancellation?.Dispose();
        if (errors is not null)
        {
            throw new AggregateException("State shutdown failed.", errors);
        }
    }

    private void DisposeCoreLocked()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _changeListeners.Clear();
        _reloadFailureListeners.Clear();
        _reloadListeners.Clear();
        _diagnostics.ClearListeners();
        _watchCancellation?.Cancel();
        foreach (var operation in _watcherOperations.Keys)
        {
            operation.RequestCancellation();
        }
    }

    private Task CaptureOperationsDrainedLocked()
    {
        if (_activeOperations == 0)
        {
            return Task.CompletedTask;
        }

        _operationsDrained ??= new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        return _operationsDrained.Task;
    }

    private OperationLease EnterOperation()
    {
        lock (_changeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeOperations++;
        }

        return new OperationLease(this);
    }

    private void ExitOperation()
    {
        lock (_changeGate)
        {
            if (--_activeOperations == 0)
            {
                _operationsDrained?.TrySetResult();
            }
        }
    }

    private readonly struct OperationLease(ConfiglueRuntime<TModel, TFragment>? owner) : IDisposable
    {
        public void Dispose() => owner?.ExitOperation();
    }

    private StateSource<TFragment>? ResolveDefaultWriteSource()
    {
        if (
            _isSingleSourceFastPath
            && _fastPathWriteSource is not null
            && ReferenceEquals(GetActiveSources(), _fastPathSources)
        )
        {
            // Single-file fast path (#231): the only writable root was pre-resolved at
            // construction, so ordinary saves skip the per-save topology scan.
            return _fastPathWriteSource;
        }

        if (_writePlan.DefaultSourceId is not { } defaultSourceId)
        {
            return null;
        }

        var source = GetActiveSources()
            .FirstOrDefault(candidate => candidate.Id == defaultSourceId);
        if (source is null)
        {
            if (_sourceSet.Sources.Any(candidate => candidate.Id == defaultSourceId))
            {
                throw new InvalidOperationException(
                    $"State source '{defaultSourceId}' has been retired from this state instance."
                );
            }

            throw new InvalidOperationException(
                $"State source '{defaultSourceId}' is not registered."
            );
        }

        if (source.Writer is null)
        {
            throw new InvalidOperationException(
                $"State source '{source.Id}' does not support writes."
            );
        }

        return source;
    }

    private async ValueTask<StateReadResult<TFragment>> ReadMigrationSourceAsync(
        StateSource<TFragment> source,
        CancellationToken cancellationToken
    )
    {
        var result = await ReadSourceAsync(source, cancellationToken).ConfigureAwait(false);
        return result.FromSource(source.Id, source.PhysicalOrigin);
    }

    private StateConflictException LogConflict(string message)
    {
        var exception = new StateConflictException(message);
        _diagnostics.Record(
            ConfiglueDiagnosticEventKind.WriteConflict,
            errorCategory: typeof(StateConflictException).FullName
        );
        return exception;
    }

    private StateSource<TFragment> FindSource(SourceId sourceId) =>
        _sourceSet.Sources.FirstOrDefault(candidate => candidate.Id == sourceId)
        ?? throw new InvalidOperationException($"State source '{sourceId}' is not registered.");

    private StateSource<TFragment>[] GetActiveSources()
    {
        return Volatile.Read(ref _activeSources);
    }

    private StateSource<TFragment>[] GetReversedActiveSources()
    {
        var activeSources = GetActiveSources();
        var reversed = new StateSource<TFragment>[activeSources.Length];
        for (var index = 0; index < activeSources.Length; index++)
        {
            reversed[index] = activeSources[activeSources.Length - 1 - index];
        }

        return reversed;
    }

    private bool IsSourceActive(SourceId sourceId)
    {
        lock (_sourceGate)
        {
            return !_retiredSourceIds.Contains(sourceId);
        }
    }

    private void RetireSourcesFromOptions(IEnumerable<SourceId> sourceIds)
    {
        TaskCompletionSource? topologyChanged = null;
        lock (_sourceGate)
        {
            var changed = false;
            foreach (var sourceId in sourceIds)
            {
                changed |= _retiredSourceIds.Add(sourceId);
            }

            if (changed)
            {
                Volatile.Write(
                    ref _activeSources,
                    _sourceSet
                        .Sources.Where(source => !_retiredSourceIds.Contains(source.Id))
                        .ToArray()
                );
                _diagnostics.SetActiveSources(_activeSources.Select(static source => source.Id));
                topologyChanged = _sourceTopologyChanged;
                _sourceTopologyChanged = NewTopologySignal();
            }
        }

        topologyChanged?.TrySetResult();
    }

    private void ValidateWritePlan(StateWritePlan writePlan)
    {
        foreach (var sourceId in writePlan.PropertyRoutes.Values)
        {
            var source = FindSource(sourceId);
            if (!IsSourceActive(source.Id))
            {
                throw new InvalidOperationException(
                    $"State source '{source.Id}' has been retired from this state instance."
                );
            }

            if (source.Writer is null)
            {
                throw new InvalidOperationException(
                    $"State source '{sourceId}' does not support writes."
                );
            }
        }
    }
}
