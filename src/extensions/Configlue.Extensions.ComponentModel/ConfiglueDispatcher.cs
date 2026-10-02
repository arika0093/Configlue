using System;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>Schedules the supplied action to run on the target thread without observing completion.</summary>
    /// <param name="action">The action to run.</param>
    /// <remarks>
    /// Prefer <see cref="InvokeAsync(Action, CancellationToken)"/> when the caller must
    /// know that the action ran or must observe a dispatcher shutdown/rejection failure.
    /// </remarks>
    void Post(Action action);

    /// <summary>
    /// Runs the supplied action on the target thread and completes only after it has run.
    /// </summary>
    /// <param name="action">The action to run.</param>
    /// <param name="cancellationToken">A token that can cancel the wait for the action.</param>
    /// <returns>
    /// A task that completes after <paramref name="action"/> has run to completion. When the
    /// caller is already on the target thread the action runs synchronously and a completed
    /// task is returned.
    /// </returns>
    /// <remarks>
    /// Implementations surface dispatcher shutdown or rejected work through the returned task
    /// (or synchronously when the fast path is taken), giving callers an observable error path.
    /// </remarks>
    ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default);
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

    /// <inheritdoc />
    public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
        ConfiglueDispatcher.InvokeAsync(this, action, cancellationToken);
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

    /// <inheritdoc />
    public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Creates dispatchers for Common component-model adapters.</summary>
public static class ConfiglueDispatcher
{
    /// <summary>Dispatches an action with completion and cancellation of queued work.</summary>
    /// <param name="dispatcher">The target dispatcher.</param>
    /// <param name="action">The action to execute.</param>
    /// <param name="cancellationToken">Cancels pending work; a running action completes normally.</param>
    /// <returns>Completion of the action, or cancellation/rejection before it starts.</returns>
    public static ValueTask InvokeAsync(
        IConfiglueDispatcher dispatcher,
        Action action,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (dispatcher.CheckAccess())
        {
            action();
            return ValueTask.CompletedTask;
        }
        return InvokeQueuedAsync(dispatcher, action, cancellationToken);
    }

    private static async ValueTask InvokeQueuedAsync(
        IConfiglueDispatcher dispatcher,
        Action action,
        CancellationToken cancellationToken
    )
    {
        var invocation = new QueuedInvocation(action, cancellationToken);
        using var registration = cancellationToken.Register(invocation.Cancel);
        dispatcher.Post(invocation.Run);
        await invocation.Completion.Task.ConfigureAwait(false);
    }

    private sealed class QueuedInvocation(Action action, CancellationToken cancellationToken)
    {
        private int _status;
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Cancel()
        {
            if (Interlocked.CompareExchange(ref _status, 2, 0) == 0)
                Completion.TrySetCanceled(cancellationToken);
        }

        public void Run()
        {
            if (Interlocked.CompareExchange(ref _status, 1, 0) != 0)
                return;
            try
            {
                action();
                Completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                Completion.TrySetException(exception);
            }
            finally
            {
                Volatile.Write(ref _status, 2);
            }
        }
    }

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
