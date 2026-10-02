using System.Collections.Concurrent;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class AsyncProfileSubscriptionTests
{
    [Test]
    public async Task OnChangeReturnsBeforeAnAsynchronousCatalogReadAndDisposalCancelsIt()
    {
        var store = new InMemoryStateSource<ConfiglueProfileCatalog>(Catalog());
        var entered = Signal();
        var cancelled = Signal();
        var registry = new TestRegistry();
        var reader = new CatalogReader(async token =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            return await store.ReadAsync(token);
        });
        await using var manager = new ConfiglueProfiledState<AppSettings, AppSettings.Fragment>(
            registry,
            new StateSource<ConfiglueProfileCatalog>("catalog", reader, writer: store)
        );
        using var subscription = manager.OnChange(_ =>
            throw new InvalidOperationException("No value expected.")
        );
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        subscription.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        registry.Default.ListenerCount.ShouldBe(0);
    }

    [Test]
    public async Task SwitchDoesNotBlockOnTheSelectedValueAndLatePreviousValuesAreSuppressed()
    {
        var registry = new TestRegistry();
        var readEntered = Signal();
        var pendingValue = new TaskCompletionSource<AppSettings>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.Work.Read = _ =>
        {
            readEntered.TrySetResult();
            return new ValueTask<AppSettings>(pendingValue.Task);
        };
        await using var manager = CreateManager(registry);
        await manager.GetProfileNamesAsync();
        var values = new ConcurrentQueue<int>();
        var receivedOther = Signal();
        using var subscription = manager.OnChange(value =>
        {
            values.Enqueue(value.RetryCount);
            if (value.RetryCount == 9)
            {
                receivedOther.TrySetResult();
            }
        });
        await registry.Default.Bound.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.SetActiveProfileAsync("Work").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.SetActiveProfileAsync("Other").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await receivedOther.Task.WaitAsync(TimeSpan.FromSeconds(5));
        registry.Work.ListenerCount.ShouldBe(0);
        registry.Work.Emit(new AppSettings { RetryCount = 4 });
        pendingValue.TrySetResult(new AppSettings { RetryCount = 5 });
        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        values.ShouldBe(new[] { 9 });
        registry.Other.ListenerCount.ShouldBe(0);
    }

    [Test]
    public async Task DisposalWaitsForOutstandingValueReadsAndSuppressesTheirResults()
    {
        var registry = new TestRegistry();
        var readEntered = Signal();
        var pendingValue = new TaskCompletionSource<AppSettings>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        CancellationToken observedToken = default;
        registry.Work.Read = token =>
        {
            observedToken = token;
            readEntered.TrySetResult();
            return new ValueTask<AppSettings>(pendingValue.Task);
        };
        await using var manager = CreateManager(registry);
        await manager.GetProfileNamesAsync();
        var values = new ConcurrentQueue<AppSettings>();
        using var subscription = manager.OnChange(values.Enqueue);
        await registry.Default.Bound.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.SetActiveProfileAsync("Work");
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = manager.DisposeAsync().AsTask();
        // Wait until cancellation reaches the pending source, rather than relying on timing.
        var cancelled = Signal();
        using var cancellationRegistration = observedToken.Register(() => cancelled.TrySetResult());
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        disposal.IsCompleted.ShouldBeFalse();
        pendingValue.TrySetResult(new AppSettings { RetryCount = 5 });
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        registry.Work.Emit(new AppSettings { RetryCount = 6 });
        values.ShouldBeEmpty();
        registry.Work.ListenerCount.ShouldBe(0);
    }

    [Test]
    public async Task InitialBindingDoesNotEmitAndWatchedChangesDetachOnDisposal()
    {
        var registry = new TestRegistry();
        await using var manager = CreateManager(registry);
        await manager.GetProfileNamesAsync();
        var values = new ConcurrentQueue<int>();
        var received = Signal();
        using var subscription = manager.OnChange(value =>
        {
            values.Enqueue(value.RetryCount);
            received.TrySetResult();
        });
        await registry.Default.Bound.Task.WaitAsync(TimeSpan.FromSeconds(5));
        values.ShouldBeEmpty();
        registry.Default.Emit(new AppSettings { RetryCount = 7 });
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        values.ShouldBe(new[] { 7 });
        subscription.Dispose();
        registry.Default.Emit(new AppSettings { RetryCount = 8 });
        values.ShouldBe(new[] { 7 });
        registry.Default.ListenerCount.ShouldBe(0);
    }

    [Test]
    public async Task ASynchronousRegistrationCallbackCanDisposeTheOwnerWithoutDeadlock()
    {
        var registry = new TestRegistry();
        registry.Default.DuringSubscribe = listener => listener(new AppSettings { RetryCount = 7 });
        await using var manager = CreateManager(registry);
        await manager.GetProfileNamesAsync();
        var disposed = Signal();
        using var subscription = manager.OnChange(_ =>
        {
            manager.Dispose();
            disposed.TrySetResult();
        });
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        registry.Default.ListenerCount.ShouldBe(0);
    }

    [Test]
    public async Task TheSwitchInitialValueCallbackCanDisposeTheOwnerWithoutDeadlock()
    {
        var registry = new TestRegistry();
        await using var manager = CreateManager(registry);
        await manager.GetProfileNamesAsync();
        var disposed = Signal();
        using var subscription = manager.OnChange(value =>
        {
            value.RetryCount.ShouldBe(9);
            manager.Dispose();
            disposed.TrySetResult();
        });
        await registry.Default.Bound.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.SetActiveProfileAsync("Work").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        registry.Work.ListenerCount.ShouldBe(0);
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ConfiglueProfileCatalog Catalog() =>
        new() { ProfileNames = ["default", "Work", "Other"], ActiveProfileName = "default" };

    private static ConfiglueProfiledState<AppSettings, AppSettings.Fragment> CreateManager(
        TestRegistry registry
    )
    {
        var store = new InMemoryStateSource<ConfiglueProfileCatalog>(Catalog());
        return new(
            registry,
            new StateSource<ConfiglueProfileCatalog>("catalog", store, writer: store)
        );
    }

    private sealed class CatalogReader(
        Func<CancellationToken, ValueTask<StateReadResult<ConfiglueProfileCatalog>>> read
    ) : ISourceReader<ConfiglueProfileCatalog>
    {
        public ValueTask<StateReadResult<ConfiglueProfileCatalog>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            return read(cancellationToken);
        }
    }

    private sealed class TestOptions : IWritableState<AppSettings>
    {
        private readonly object _gate = new();
        private Action<AppSettings>? _listeners;
        public TaskCompletionSource Bound { get; } = Signal();
        public Action<Action<AppSettings>>? DuringSubscribe { get; set; }
        public Func<CancellationToken, ValueTask<AppSettings>> Read { get; set; } =
            _ => ValueTaskCompat.FromResult(new AppSettings { RetryCount = 9 });
        public int ListenerCount
        {
            get
            {
                lock (_gate)
                {
                    return _listeners?.GetInvocationList().Length ?? 0;
                }
            }
        }

        public IDisposable OnChange(Action<AppSettings> listener)
        {
            lock (_gate)
            {
                _listeners += listener;
            }
            Bound.TrySetResult();
            DuringSubscribe?.Invoke(listener);
            return new ListenerHandle(() =>
            {
                lock (_gate)
                {
                    _listeners -= listener;
                }
            });
        }

        public void Emit(AppSettings value)
        {
            Action<AppSettings>? listeners;
            lock (_gate)
            {
                listeners = _listeners;
            }
            listeners?.Invoke(value);
        }

        public ValueTask<AppSettings> GetValueAsync(
            CancellationToken cancellationToken = default
        ) => Read(cancellationToken);

        public ValueTask<StateWriteReceipt> SaveAsync(
            IConfiglueModelPatch<AppSettings> patch,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class ListenerHandle(Action detach) : IDisposable
    {
        private Action? _detach = detach;

        public void Dispose() => Interlocked.Exchange(ref _detach, null)?.Invoke();
    }

    private sealed class TestRegistry : IConfiglueStateRegistry<AppSettings>
    {
        public TestOptions Default { get; } = new();
        public TestOptions Work { get; } = new();
        public TestOptions Other { get; } = new();
        public IReadOnlyCollection<string> StateNames => new[] { "default", "Work", "Other" };
        public event Action<string, IWritableState<AppSettings>>? StateAdded
        {
            add { }
            remove { }
        }
        public event Action<string>? StateRemoved
        {
            add { }
            remove { }
        }

        public IWritableState<AppSettings> Get(string profileName) =>
            profileName switch
            {
                "default" => Default,
                "Work" => Work,
                "Other" => Other,
                _ => throw new KeyNotFoundException(),
            };

        public bool TryGet(string profileName, out IWritableState<AppSettings>? options)
        {
            options = profileName is "default" or "Work" or "Other" ? Get(profileName) : null;
            return options is not null;
        }

        public bool TryAdd(string profileName) => false;

        public bool TryRemove(string profileName) => false;

        public void Clear() { }

        public void Dispose() { }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
