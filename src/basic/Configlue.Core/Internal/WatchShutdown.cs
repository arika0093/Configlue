namespace Configlue.Internal;

/// <summary>Stops owned watches without disposing a backend that still has an active lease.</summary>
internal sealed class WatchShutdown
{
    private readonly TaskCompletionSource<bool> _shutdown = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public void Signal() => _shutdown.TrySetResult(true);

    public async ValueTask WaitAsync(
        Func<CancellationToken, ValueTask> waitForChange,
        CancellationToken cancellationToken
    )
    {
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var watch = waitForChange(watchCancellation.Token).AsTask();
        var completed = await Task.WhenAny(watch, _shutdown.Task).ConfigureAwait(false);
        var stopping = completed == _shutdown.Task;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? cancellationFailure = null;
        try
        {
            if (stopping)
                await watchCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cancellationFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                exception
            );
        }

        // The caller keeps its lease until cancellation and backend cleanup finish,
        // including when a cancellation callback itself fails.
        try
        {
            await watch.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (stopping && !cancellationToken.IsCancellationRequested)
        {
            // Resource disposal historically wakes watchers successfully. Caller
            // cancellation still propagates with its original meaning.
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
        cancellationFailure?.Throw();
    }
}
