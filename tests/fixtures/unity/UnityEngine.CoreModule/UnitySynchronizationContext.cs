using System.Collections.Concurrent;

namespace UnityEngine;

/// <summary>
/// Minimal stand-in for Unity's main-thread synchronization context. Posted work is
/// queued until <see cref="RunPending"/> simulates a main-thread pump.
/// </summary>
public sealed class UnitySynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();

    /// <summary>The number of queued callbacks.</summary>
    public int PendingCount => _pending.Count;

    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        _pending.Enqueue((d, state));
    }

    /// <summary>Drains queued callbacks in FIFO order.</summary>
    public void RunPending()
    {
        while (_pending.TryDequeue(out var work))
        {
            work.Callback(work.State);
        }
    }
}
