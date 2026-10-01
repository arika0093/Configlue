using Configlue.Extensions.ComponentModel;
using Microsoft.UI.Dispatching;

namespace Configlue.Hosting.WinUI;

/// <summary>Dispatches shared ComponentModel updates to an explicitly selected WinUI queue.</summary>
/// <remarks>
/// Bind an adapter to the window's DispatcherQueue. The application owns the queue and
/// coordinates its shutdown; this adapter creates no global dispatcher or UI thread.
/// </remarks>
public sealed class WinUIConfiglueDispatcher : IConfiglueDispatcher
{
    private readonly DispatcherQueue _queue;

    /// <summary>Creates an adapter for the queue owning the bound window.</summary>
    /// <param name="queue">The window's UI dispatcher queue.</param>
    public WinUIConfiglueDispatcher(DispatcherQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
    }

    /// <inheritdoc />
    public bool CheckAccess() => _queue.HasThreadAccess;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The queue rejected the callback, for example during shutdown.</exception>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_queue.TryEnqueue(() => action()))
            throw new InvalidOperationException(
                "The WinUI dispatcher queue rejected the callback."
            );
    }
}
