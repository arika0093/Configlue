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
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _changeListeners.Clear();
            _reloadFailureListeners.Clear();
            _reloadListeners.Clear();
            _watchCancellation?.Cancel();
            foreach (var subscription in _subjectSubscriptions.Keys.ToArray())
            {
                subscription.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var subjectWatchTasks = _subjectSubscriptions
            .Keys.Select(static subscription => subscription.Completion)
            .ToArray();
        Dispose();
        Task? watchTask;
        Task operationsDrained;
        lock (_changeGate)
        {
            watchTask = _watchTask;
            if (_activeOperations == 0)
            {
                operationsDrained = Task.CompletedTask;
            }
            else
            {
                _operationsDrained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                operationsDrained = _operationsDrained.Task;
            }
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
        foreach (var subjectWatchTask in subjectWatchTasks)
        {
            try
            {
                await subjectWatchTask.ConfigureAwait(false);
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
        if (_writePlan.DefaultSourceId is not { } defaultSourceId)
        {
            return null;
        }

        var source = GetActiveSources()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Id, defaultSourceId, StringComparison.Ordinal)
            );
        if (source is null)
        {
            if (
                _sourceSet.Sources.Any(candidate =>
                    string.Equals(candidate.Id, defaultSourceId, StringComparison.Ordinal)
                )
            )
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

    private async ValueTask<StateWriteResult> WriteStateAsync(
        StateSource<TFragment> target,
        ISourceWriter<TFragment> writer,
        StateWriteRequest<TFragment> request,
        string operation,
        CancellationToken cancellationToken,
        string? relatedSourceIds = null,
        EventId? eventId = null
    )
    {
        var writeEvent = eventId ?? PhysicalWriteEvent;
        _logger?.LogInformation(
            writeEvent,
            "{Operation} for {ModelType} state {StateName} through source {SourceId} at resource {ResourceId}; related sources {RelatedSourceIds}.",
            operation,
            typeof(TModel).FullName,
            _stateName,
            target.Id,
            target.ResourceId?.Value,
            relatedSourceIds
        );
        StateWriteResult result;
        try
        {
            var context = GetResourceContext(target);
            result = _subjectContext.Value is not null
                ? await target.WriteAsync(context, request, cancellationToken).ConfigureAwait(false)
                : await writer
                    .WriteAsync(context, request, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Preserve the writer's exception type for callers that classify conflicts or retries.
#pragma warning disable S2139
        catch (Exception exception)
        {
            _logger?.LogError(
                writeEvent,
                exception,
                "{Operation} failed for {ModelType} state {StateName} through source {SourceId} at resource {ResourceId}; related sources {RelatedSourceIds}.",
                operation,
                typeof(TModel).FullName,
                _stateName,
                target.Id,
                target.ResourceId?.Value,
                relatedSourceIds
            );
            throw;
        }
#pragma warning restore S2139

        _logger?.LogDebug(
            writeEvent,
            "{Operation} completed through source {SourceId} at resource {ResourceId} for {ModelType} state {StateName}.",
            operation,
            target.Id,
            target.ResourceId?.Value,
            typeof(TModel).FullName,
            _stateName
        );
        return result;
    }

    private async ValueTask<StateReadResult<TFragment>> ReadMigrationSourceAsync(
        StateSource<TFragment> source,
        CancellationToken cancellationToken
    )
    {
        StateReadResult<TFragment> result;
        try
        {
            result = await ReadSourceAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Preserve the reader's exception type so a codec's recovery policy remains effective.
#pragma warning disable S2139
        catch (Exception exception)
        {
            _logger?.LogError(
                MigrationEvent,
                exception,
                "Reading migration source {SourceId} failed for {ModelType} state {StateName} at {PhysicalOrigin} ({ResourceId}).",
                source.Id,
                typeof(TModel).FullName,
                _stateName,
                source.PhysicalOrigin,
                source.ResourceId?.Value
            );
            throw;
        }
#pragma warning restore S2139

        var sourcedResult = result.FromSource(source.Id, source.PhysicalOrigin);
        _logger?.LogDebug(
            MigrationEvent,
            "Migration source {SourceId} returned {ReadStatus} for {ModelType} state {StateName} at {PhysicalOrigin} ({ResourceId}).",
            source.Id,
            sourcedResult.Status,
            typeof(TModel).FullName,
            _stateName,
            source.PhysicalOrigin,
            source.ResourceId?.Value
        );
        return sourcedResult;
    }

    private StateConflictException LogConflict(string message)
    {
        var exception = new StateConflictException(message);
        _logger?.LogWarning(
            ConflictEvent,
            exception,
            "A configuration write conflict occurred for {ModelType} state {StateName}: {Conflict}.",
            typeof(TModel).FullName,
            _stateName,
            message
        );
        return exception;
    }

    private StateSource<TFragment> FindSource(string sourceId) =>
        _sourceSet.Sources.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, sourceId, StringComparison.Ordinal)
        ) ?? throw new InvalidOperationException($"State source '{sourceId}' is not registered.");

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

    private bool IsSourceActive(string sourceId)
    {
        lock (_sourceGate)
        {
            return !_retiredSourceIds.Contains(sourceId);
        }
    }

    private void RetireSourcesFromOptions(IEnumerable<string> sourceIds)
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
