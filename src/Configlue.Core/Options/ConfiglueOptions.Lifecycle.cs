using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace Configlue;

public sealed partial class ConfiglueOptions<TModel, TFragment>
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
            _watchCancellation?.Cancel();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
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
            throw new AggregateException("Options shutdown failed.", errors);
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

    private readonly struct OperationLease(ConfiglueOptions<TModel, TFragment>? owner) : IDisposable
    {
        public void Dispose() => owner?.ExitOperation();
    }

    private StateSource<TFragment> SelectWriteSource(bool allowPriorityFallback = false)
    {
        var activeSources = GetActiveSources();
        StateSource<TFragment>? source;
        if (_writeRoute.SourceId is { } id)
        {
            source = activeSources.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal)
            );
        }
        else
        {
            source = null;
            foreach (var candidate in activeSources)
            {
                if (
                    candidate.Writer is null
                    || candidate.ExplicitOnly
                    || candidate.OwnedPropertyPaths.Count > 0
                )
                {
                    continue;
                }

                if (source is not null && !allowPriorityFallback)
                {
                    throw new InvalidOperationException(
                        "Multiple writable state sources are registered. Configure a default write route."
                    );
                }

                source ??= candidate;
            }

            if (source is null && allowPriorityFallback)
            {
                source = activeSources.FirstOrDefault(candidate =>
                    candidate.Writer is not null && !candidate.ExplicitOnly
                );
            }
        }

        if (source is null)
        {
            if (
                _writeRoute.SourceId is { } retiredId
                && _sourceSet.Sources.Any(candidate =>
                    string.Equals(candidate.Id, retiredId, StringComparison.Ordinal)
                )
            )
            {
                throw new InvalidOperationException(
                    $"State source '{retiredId}' has been retired from this options instance."
                );
            }

            throw new InvalidOperationException(
                _writeRoute.SourceId is { } sourceId
                    ? $"State source '{sourceId}' is not registered."
                    : "No writable state source is registered."
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

    private StateSource<TFragment>? TrySelectDefaultWriteSource()
    {
        if (_writeRoute.SourceId is not null)
        {
            return SelectWriteSource();
        }

        var activeSources = GetActiveSources();
        StateSource<TFragment>? source = null;
        foreach (var candidate in activeSources)
        {
            if (
                candidate.Writer is null
                || candidate.ExplicitOnly
                || candidate.OwnedPropertyPaths.Count > 0
            )
            {
                continue;
            }

            if (source is not null)
            {
                return null;
            }

            source = candidate;
        }

        return source;
    }

    private async ValueTask<StateWriteResult> WriteStateAsync(
        StateSource<TFragment> target,
        IStateWriter<TFragment> writer,
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
            "{Operation} for {ModelType} options {OptionsName} through source {SourceId} at resource {ResourceId}; related sources {RelatedSourceIds}.",
            operation,
            typeof(TModel).FullName,
            _optionsName,
            target.Id,
            target.ResourceId?.Value,
            relatedSourceIds
        );
        StateWriteResult result;
        try
        {
            result = await writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
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
                "{Operation} failed for {ModelType} options {OptionsName} through source {SourceId} at resource {ResourceId}; related sources {RelatedSourceIds}.",
                operation,
                typeof(TModel).FullName,
                _optionsName,
                target.Id,
                target.ResourceId?.Value,
                relatedSourceIds
            );
            throw;
        }
#pragma warning restore S2139

        _logger?.LogDebug(
            writeEvent,
            "{Operation} completed through source {SourceId} at resource {ResourceId} for {ModelType} options {OptionsName}.",
            operation,
            target.Id,
            target.ResourceId?.Value,
            typeof(TModel).FullName,
            _optionsName
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
            result = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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
                "Reading migration source {SourceId} failed for {ModelType} options {OptionsName} at {PhysicalOrigin} ({ResourceId}).",
                source.Id,
                typeof(TModel).FullName,
                _optionsName,
                source.PhysicalOrigin,
                source.ResourceId?.Value
            );
            throw;
        }
#pragma warning restore S2139

        var sourcedResult = result.FromSource(source.Id, source.PhysicalOrigin);
        _logger?.LogDebug(
            MigrationEvent,
            "Migration source {SourceId} returned {ReadStatus} for {ModelType} options {OptionsName} at {PhysicalOrigin} ({ResourceId}).",
            source.Id,
            sourcedResult.Status,
            typeof(TModel).FullName,
            _optionsName,
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
            "A configuration write conflict occurred for {ModelType} options {OptionsName}: {Conflict}.",
            typeof(TModel).FullName,
            _optionsName,
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
        foreach (var (propertyPath, sourceId) in writePlan.PropertyRoutes)
        {
            var path = propertyPath.Split('.', StringSplitOptions.None);
            var schema = TModel.ConfiglueSchema;
            for (var index = 0; index < path.Length; index++)
            {
                var member = schema.Members.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, path[index], StringComparison.Ordinal)
                );
                if (string.IsNullOrEmpty(member.Name))
                {
                    throw new ArgumentException(
                        $"Write plan path '{propertyPath}' refers to unknown member '{path[index]}' in '{schema.Id}'.",
                        nameof(writePlan)
                    );
                }

                if (index < path.Length - 1)
                {
                    schema =
                        member.NestedSchemaFactory?.Invoke()
                        ?? throw new ArgumentException(
                            $"Write plan path '{propertyPath}' continues through non-nested member '{member.Name}'.",
                            nameof(writePlan)
                        );
                }
            }

            var source = FindSource(sourceId);
            if (!IsSourceActive(source.Id))
            {
                throw new InvalidOperationException(
                    $"State source '{source.Id}' has been retired from this options instance."
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
