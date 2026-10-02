using Configlue.Extensions.ComponentModel;
using UnityEngine;

namespace Configlue.Hosting.Unity;

/// <summary>Dispatches shared ComponentModel updates through the initialized Unity 6 main-thread context.</summary>
/// <remarks>Construct on the Unity main thread. Posts are rejected after play/player exit; accepted callbacks are skipped if exit happens before execution.</remarks>
public sealed class UnityConfiglueDispatcher : IConfiglueDispatcher
{
    private readonly SynchronizationContext _context;
    private readonly CancellationToken _exitCancellation;

    /// <summary>Captures Unity's native main-thread context and the current play/player exit token.</summary>
    public UnityConfiglueDispatcher()
    {
        EnsureMainThread();
        var context = SynchronizationContext.Current;
        if (context?.GetType().FullName != "UnityEngine.UnitySynchronizationContext")
            throw new InvalidOperationException(
                "Unity's main-thread synchronization context is not initialized."
            );
        _context = context;
        _exitCancellation = Application.exitCancellationToken;
    }

    internal static void EnsureMainThread()
    {
        if (!Awaitable.MainThreadAsync().GetAwaiter().IsCompleted)
            throw new InvalidOperationException(
                "Initialize the Unity host adapter on Unity's main thread."
            );
    }

    /// <inheritdoc />
    public bool CheckAccess() =>
        !_exitCancellation.IsCancellationRequested
        && Awaitable.MainThreadAsync().GetAwaiter().IsCompleted;

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException">The captured play/player lifetime has exited.</exception>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _exitCancellation.ThrowIfCancellationRequested();
        _context.Post(
            static state =>
            {
                var work = (PostedWork)state!;
                if (!work.ExitCancellation.IsCancellationRequested)
                    work.Action();
            },
            new PostedWork(action, _exitCancellation)
        );
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException">The captured play/player lifetime has exited.</exception>
    public async ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _exitCancellation
        );
        await ConfiglueDispatcher.InvokeAsync(this, action, linked.Token).ConfigureAwait(false);
    }

    private sealed record PostedWork(Action Action, CancellationToken ExitCancellation);
}
