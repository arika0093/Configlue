using Avalonia.Threading;
using Configlue.Extensions.ComponentModel;

namespace Configlue.Hosting.Avalonia;

/// <summary>Dispatches shared ComponentModel updates onto an Avalonia UI dispatcher.</summary>
/// <remarks>The application owns the dispatcher and its lifetime. This adapter does not change desktop storage paths.</remarks>
public sealed class AvaloniaConfiglueDispatcher : IConfiglueDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>Creates an adapter for an explicitly selected Avalonia dispatcher.</summary>
    /// <param name="dispatcher">The dispatcher owning the application's bound UI.</param>
    public AvaloniaConfiglueDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    /// <summary>Creates an adapter for <see cref="Dispatcher.UIThread"/>.</summary>
    public AvaloniaConfiglueDispatcher()
        : this(Dispatcher.UIThread) { }

    /// <inheritdoc />
    public bool CheckAccess() => _dispatcher.CheckAccess();

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _dispatcher.Post(action, DispatcherPriority.Normal);
    }
}
