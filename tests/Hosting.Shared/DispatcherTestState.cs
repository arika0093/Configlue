namespace Configlue.Hosting.Tests;

[ConfiglueModel("host-dispatch-tests", Version = 1)]
public partial class HostSettings
{
    public int Counter { get; set; }
}

internal sealed class DispatcherTestState
    : IReadOnlyState<HostSettings>,
        IConfiglueStateSnapshotRuntime<HostSettings>
{
    private HostSettings _value = new() { Counter = 1 };
    private Action<HostSettings>? _listener;

    public IDisposable OnChange(Action<HostSettings> listener)
    {
        _listener = listener;
        return new Subscription(() => _listener = null);
    }

    public ValueTask<HostSettings> GetValueAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Volatile.Read(ref _value));

    public ValueTask<StateSnapshot<HostSettings>> GetSnapshotAsync(
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult(new StateSnapshot<HostSettings>(Volatile.Read(ref _value), null));

    internal void Push(int counter)
    {
        var value = new HostSettings { Counter = counter };
        Volatile.Write(ref _value, value);
        _listener?.Invoke(value);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
