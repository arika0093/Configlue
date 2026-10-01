namespace Configlue.Tests;

/// <summary>Counts synchronous and asynchronous disposal for ownership tests.</summary>
internal sealed class AsyncDisposableProbe : IDisposable, IAsyncDisposable
{
    public int DisposeCallCount { get; private set; }

    public int DisposeAsyncCallCount { get; private set; }

    public void Dispose() => DisposeCallCount++;

    public ValueTask DisposeAsync()
    {
        DisposeAsyncCallCount++;
        return ValueTask.CompletedTask;
    }
}
