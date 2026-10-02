using System.Windows.Threading;
using Configlue.Extensions.ComponentModel;

namespace Configlue.Hosting.Wpf;

/// <summary>Dispatches ComponentModel updates to an explicitly selected WPF UI thread.</summary>
/// <remarks>
/// Use the dispatcher belonging to the bound window. Each instance is independent of
/// Application.Current and supports applications with multiple UI dispatchers.
/// Queued callbacks follow WPF shutdown semantics and may be aborted during shutdown.
/// </remarks>
public sealed class WpfConfiglueDispatcher : IConfiglueDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>Creates an adapter for the dispatcher owning the bound UI.</summary>
    /// <param name="dispatcher">The application's window or UI dispatcher.</param>
    public WpfConfiglueDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public bool CheckAccess() =>
        !_dispatcher.HasShutdownStarted
        && !_dispatcher.HasShutdownFinished
        && _dispatcher.CheckAccess();

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The dispatcher has begun shutdown or rejected the callback.</exception>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            throw new InvalidOperationException("The WPF dispatcher is shutting down.");
        var operation = _dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
        if (operation.Status == DispatcherOperationStatus.Aborted)
            throw new InvalidOperationException("The WPF dispatcher rejected the callback.");
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The dispatcher has begun shutdown.</exception>
    public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            throw new InvalidOperationException("The WPF dispatcher is shutting down.");
        if (_dispatcher.CheckAccess())
        {
            action();
            return ValueTask.CompletedTask;
        }

        var operation = _dispatcher.InvokeAsync(
            action,
            DispatcherPriority.DataBind,
            cancellationToken
        );
        return new ValueTask(operation.Task);
    }
}
