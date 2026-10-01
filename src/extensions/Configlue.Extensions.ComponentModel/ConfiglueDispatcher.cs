using System;
using System.Threading;

namespace Configlue.Extensions.ComponentModel;

/// <summary>
/// Marshals observable updates onto the thread that owns a bound UI.
/// </summary>
/// <remarks>
/// Reload and source notifications may arrive from non-UI threads. Adapters that
/// implement this interface let a host/framework integration (WPF
/// <c>Dispatcher</c>, WinUI <c>DispatcherQueue</c>, Avalonia <c>Dispatcher.UIThread</c>,
/// MAUI <c>MainThread</c>, Unity/Godot main thread, ...) provide the correct target
/// thread without Core or this package taking a dependency on any UI framework.
/// </remarks>
public interface IConfiglueDispatcher
{
    /// <summary>Gets a value indicating whether the caller is already on the target thread.</summary>
    /// <returns><see langword="true"/> when the caller may mutate bound state directly.</returns>
    bool CheckAccess();

    /// <summary>Schedules the supplied action to run on the target thread.</summary>
    /// <param name="action">The action to run.</param>
    void Post(Action action);
}

/// <summary>
/// An <see cref="IConfiglueDispatcher"/> that forwards to a captured
/// <see cref="SynchronizationContext"/>, which is sufficient for many UI stacks.
/// </summary>
public sealed class SynchronizationContextConfiglueDispatcher : IConfiglueDispatcher
{
    private readonly SynchronizationContext _context;

    /// <summary>Creates a dispatcher over the supplied synchronization context.</summary>
    /// <param name="context">The context that owns the bound UI.</param>
    public SynchronizationContextConfiglueDispatcher(SynchronizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public bool CheckAccess() => ReferenceEquals(SynchronizationContext.Current, _context);

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _context.Post(_ => action(), null);
    }
}

/// <summary>An <see cref="IConfiglueDispatcher"/> that runs actions inline.</summary>
public sealed class ImmediateConfiglueDispatcher : IConfiglueDispatcher
{
    /// <summary>Gets the shared inline dispatcher instance.</summary>
    public static ImmediateConfiglueDispatcher Instance { get; } = new();

    private ImmediateConfiglueDispatcher() { }

    /// <inheritdoc />
    public bool CheckAccess() => true;

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}

/// <summary>Creates dispatchers for Common component-model adapters.</summary>
public static class ConfiglueDispatcher
{
    /// <summary>
    /// Gets an inline dispatcher that runs actions on the calling thread.
    /// </summary>
    public static IConfiglueDispatcher Immediate => ImmediateConfiglueDispatcher.Instance;

    /// <summary>
    /// Captures the current <see cref="SynchronizationContext"/> when one is present,
    /// otherwise returns the inline dispatcher.
    /// </summary>
    /// <returns>A dispatcher suitable for the current thread.</returns>
    public static IConfiglueDispatcher FromCurrentSynchronizationContext() =>
        SynchronizationContext.Current is { } context
            ? new SynchronizationContextConfiglueDispatcher(context)
            : Immediate;
}
