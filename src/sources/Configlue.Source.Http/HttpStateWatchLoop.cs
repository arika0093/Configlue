using Configlue.State;

namespace Configlue.Source.Http;

/// <summary>Shared SSE watch loop with waiter fan-out and exponential reconnect backoff.</summary>
/// <remarks>
/// Converges through the transport GET and writes the result into the shared baseline
/// cache (revision and baseline bytes via <c>CacheGetResult</c>). The cache instance is
/// shared with the read/write paths, so watch convergence replaces the PATCH baseline
/// (cross-talk by design): the next write re-derives its patch from the converged bytes.
/// </remarks>
internal sealed class HttpStateWatchLoop<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly HttpStateTransport<TFragment> _transport;
    private readonly HttpSseClient _sse;
    private readonly HttpStateBaselineCache _baseline;
    private readonly TimeSpan _reconnectInitialDelay;
    private readonly TimeSpan _reconnectMaxDelay;
    private readonly object _watchGate = new();
    private readonly List<ChangeWaiter> _changeWaiters = [];
    private CancellationTokenSource? _sharedWatchCancellation;
    private Task? _sharedWatchTask;

    public HttpStateWatchLoop(
        HttpStateTransport<TFragment> transport,
        HttpSseClient sse,
        HttpStateBaselineCache baseline,
        TimeSpan reconnectInitialDelay,
        TimeSpan reconnectMaxDelay
    )
    {
        _transport = transport;
        _sse = sse;
        _baseline = baseline;
        _reconnectInitialDelay = reconnectInitialDelay;
        _reconnectMaxDelay = reconnectMaxDelay;
    }

    public async ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var baseline = _baseline.GetRevision();
        if (baseline is null)
        {
            var initial = await _transport.GetAsync(null, cancellationToken).ConfigureAwait(false);
            _baseline.CacheGetResult(initial.Revision, initial.Content);
            baseline = initial.Revision;
        }

        if (!string.Equals(observedRevision, baseline, StringComparison.Ordinal))
        {
            return;
        }

        var waiter = new ChangeWaiter(baseline);
        lock (_watchGate)
        {
            var current = _baseline.GetRevision();
            if (!string.Equals(baseline, current, StringComparison.Ordinal))
            {
                return;
            }

            _changeWaiters.Add(waiter);
            if (_sharedWatchTask is null)
            {
                StartSharedWatchLoopLocked();
            }
        }

        try
        {
            await waiter.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RemoveChangeWaiter(waiter);
        }
    }

    private void StartSharedWatchLoopLocked()
    {
        var cancellation = new CancellationTokenSource();
        _sharedWatchCancellation = cancellation;
        _sharedWatchTask = Task.Run(
            () => RunSharedWatchLoopAsync(cancellation),
            CancellationToken.None
        );
    }

    private async Task RunSharedWatchLoopAsync(CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        var backoff = _reconnectInitialDelay;
        try
        {
            while (true)
            {
                string? baseline;
                lock (_watchGate)
                {
                    if (_changeWaiters.Count == 0)
                    {
                        return;
                    }

                    baseline = _baseline.GetRevision();
                    CompleteChangedWaitersLocked(baseline);
                    if (_changeWaiters.Count == 0)
                    {
                        continue;
                    }
                }

                string? eventRevision;
                try
                {
                    // Convergence check before opening SSE: closes the race where the server
                    // changes between the watcher's baseline read and the SSE subscription.
                    var preCheck = await _transport
                        .GetAsync(null, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(baseline, preCheck.Revision, StringComparison.Ordinal))
                    {
                        _baseline.CacheGetResult(preCheck.Revision, preCheck.Content);
                        lock (_watchGate)
                        {
                            CompleteChangedWaitersLocked(preCheck.Revision);
                        }

                        backoff = _reconnectInitialDelay;
                        continue;
                    }

                    eventRevision = await _sse.WaitForInvalidationAsync(baseline, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // Transport failure: converge via re-read, then back off before reopening SSE.
                    try
                    {
                        var converged = await _transport
                            .GetAsync(null, cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.Equals(baseline, converged.Revision, StringComparison.Ordinal))
                        {
                            _baseline.CacheGetResult(converged.Revision, converged.Content);
                            lock (_watchGate)
                            {
                                CompleteChangedWaitersLocked(converged.Revision);
                            }

                            backoff = _reconnectInitialDelay;
                            continue;
                        }
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        // Convergence re-reads failed; fall through to backoff and SSE reopen.
                        // The shared watch loop must survive transient transport failures.
                        System.Diagnostics.Debug.WriteLine(exception);
                    }

                    try
                    {
                        await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    backoff = HttpStateProtocol.NextBackoff(backoff, _reconnectMaxDelay);
                    continue;
                }

                // SSE signaled a change: re-read to converge even if events were missed.
                backoff = _reconnectInitialDelay;
                HttpTransportGetResult<TFragment> convergedResult;
                try
                {
                    convergedResult = await _transport
                        .GetAsync(null, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    try
                    {
                        await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    backoff = HttpStateProtocol.NextBackoff(backoff, _reconnectMaxDelay);
                    continue;
                }

                var newRevision = convergedResult.Revision ?? eventRevision;
                if (!string.Equals(baseline, newRevision, StringComparison.Ordinal))
                {
                    _baseline.CacheGetResult(convergedResult.Revision, convergedResult.Content);
                    lock (_watchGate)
                    {
                        CompleteChangedWaitersLocked(newRevision);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                lock (_watchGate)
                {
                    foreach (var waiter in _changeWaiters)
                    {
                        waiter.Completion.TrySetException(exception);
                    }

                    _changeWaiters.Clear();
                }
            }
        }
        finally
        {
            lock (_watchGate)
            {
                if (ReferenceEquals(_sharedWatchCancellation, cancellation))
                {
                    _sharedWatchCancellation = null;
                    _sharedWatchTask = null;
                    if (_changeWaiters.Count > 0)
                    {
                        StartSharedWatchLoopLocked();
                    }
                }
            }

            cancellation.Dispose();
        }
    }

    private void CompleteChangedWaitersLocked(string? currentRevision)
    {
        for (var index = _changeWaiters.Count - 1; index >= 0; index--)
        {
            var waiter = _changeWaiters[index];
            if (string.Equals(waiter.Baseline, currentRevision, StringComparison.Ordinal))
            {
                continue;
            }

            _changeWaiters.RemoveAt(index);
            waiter.Completion.TrySetResult();
        }
    }

    private void RemoveChangeWaiter(ChangeWaiter waiter)
    {
        lock (_watchGate)
        {
            _changeWaiters.Remove(waiter);
            if (_changeWaiters.Count == 0)
            {
                _sharedWatchCancellation?.Cancel();
            }
        }
    }

    private sealed class ChangeWaiter(string? baseline)
    {
        public string? Baseline { get; } = baseline;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
