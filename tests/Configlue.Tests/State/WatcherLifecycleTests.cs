using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed partial class WatcherLifecycleTests
{
    [Test]
    public async Task RetiringActiveSourceWakesFixedSubjectWatcherAndRebindsToRemainingSource()
    {
        var legacyStore = new InMemoryStateSource<AppSettings.Fragment>(Fragment("legacy"));
        var currentStore = new InMemoryStateSource<AppSettings.Fragment>();
        var legacyWatcher = new ManualWatcher();
        var currentWatcher = new ManualWatcher();
        var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("legacy", legacyStore, new StateSourceOptions<AppSettings.Fragment> { Priority = 100, Watcher = legacyWatcher }),
                new StateSource<AppSettings.Fragment>("current", currentStore, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = currentStore, Watcher = currentWatcher }),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        ISubjectState<AppSettings> subjectState = runtime;
        var received = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = subjectState
            .ForSubject(new TestSubject())
            .OnChange(value => received.TrySetResult(value.Label));

        await legacyWatcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await currentWatcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        IConfiglueRuntimeState<AppSettings> migratable = runtime;
        var targets = new Dictionary<SourceId, Func<IConfiglueFragment, IConfiglueFragment>>()
        {
            [SourceId.From("current")] = static fragment => fragment,
        };
        var migration = migratable
            .MigrateSourcesToTargetsAsync([SourceId.From("legacy")], targets, retireSources: true)
            .AsTask();
        await migration.WaitAsync(TimeSpan.FromSeconds(5));

        await legacyWatcher.Canceled.WaitAsync(TimeSpan.FromSeconds(5));
        await currentWatcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        legacyWatcher.WaitCount.ShouldBe(1);

        currentStore.Set(Fragment("changed"));
        currentWatcher.Signal();
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("changed");
        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RegisteringSubjectWatcherWhileShutdownIsRunningIsRejected()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(Fragment("initial"));
        var watcher = new ManualWatcher();
        var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new StateSource<AppSettings.Fragment>("store", store, new StateSourceOptions<AppSettings.Fragment> { Watcher = watcher })]),
            onChangeDebounce: TimeSpan.Zero
        );
        ISubjectState<AppSettings> subjectState = runtime;
        var subscription = subjectState.ForSubject(new TestSubject()).OnChange(_ => { });
        await watcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var shutdown = runtime.DisposeAsync().AsTask();
        await watcher.Canceled.WaitAsync(TimeSpan.FromSeconds(5));

        Should.Throw<ObjectDisposedException>(() =>
            subjectState.ForSubject(new TestSubject()).OnChange(_ => { })
        );
        subscription.Dispose();
        subscription.Dispose();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task CurrentSubjectInvalidationBurstUsesOneCoalescingWorker()
    {
        var subjectState = new CountingSubjectState();
        var accessor = new BlockingAccessor();
        using var subscription = new CurrentSubjectState<AppSettings>(
            subjectState,
            accessor
        ).OnChange(_ => { });

        await accessor.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 10; index++)
        {
            accessor.Invalidate();
        }

        accessor.Release();
        await subjectState.SecondSubscription.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        accessor.CallCount.ShouldBe(2);
        subscription.Dispose();
        subscription.Dispose();
    }

    [Test]
    public async Task CurrentSubjectWatcherRetriesInitialBindFailureWithoutChangeSource()
    {
        var subjectState = new RetryOnceSubjectState();
        using var subscription = new CurrentSubjectState<AppSettings>(
            subjectState,
            new StaticSubjectAccessor()
        ).OnChange(_ => { });

        await subjectState.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        subjectState.AttemptCount.ShouldBe(2);
    }

    [Test]
    public async Task CurrentSubjectWatcherRetriesTransientSubjectResolutionFailureWithBackoff()
    {
        var subjectState = new RetryOnceSubjectState(failFirstBind: false);
        var accessor = new RetryOnceSubjectAccessor();
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var received = new TaskCompletionSource<AppSettings>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = new CurrentSubjectState<AppSettings>(
            subjectState,
            accessor
        ).OnChange(value => received.TrySetResult(value));

        await accessor.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await subjectState.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        System
            .Diagnostics.Stopwatch.GetElapsedTime(startedAt)
            .ShouldBeGreaterThanOrEqualTo(SubjectChangeSubscriptionRetryPolicy.InitialDelay);
        accessor.CallCount.ShouldBe(2);
        subjectState.Raise(new AppSettings());
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldNotBeNull();
    }

    [Test]
    public async Task DisposingCurrentSubjectWatcherCancelsPendingBindRetry()
    {
        var subjectState = new RetryOnceSubjectState();
        var subscription = new CurrentSubjectState<AppSettings>(
            subjectState,
            new StaticSubjectAccessor()
        ).OnChange(_ => { });
        await subjectState.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));

        subscription.Dispose();
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        subjectState.AttemptCount.ShouldBe(1);
    }

    [Test]
    public async Task SubjectWatcherSurvivesListenerFailuresAndDisposalIsIdempotent()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(Fragment("zero"));
        var watcher = new ManualWatcher();
        var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new StateSource<AppSettings.Fragment>("store", store, new StateSourceOptions<AppSettings.Fragment> { Watcher = watcher })]),
            onChangeDebounce: TimeSpan.Zero
        );
        ISubjectState<AppSettings> subjectState = runtime;
        var received = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var subscription = subjectState
            .ForSubject(new TestSubject())
            .OnChange(value =>
            {
                if (value.Label == "one")
                {
                    throw new InvalidOperationException("The listener failed.");
                }

                received.TrySetResult(value.Label);
            });

        await watcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        store.Set(Fragment("one"));
        watcher.Signal();
        await watcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        store.Set(Fragment("two"));
        watcher.Signal();
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("two");

        subscription.Dispose();
        subscription.Dispose();
        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task OnChange_ObservesExternalChangeThatRacesWatchLoopStartup()
    {
        // Regression test for the cold-start lost-wakeup window: an external change
        // landing between subscription and the watch loop's first resolution read was
        // adopted silently as the baseline and never reported. The first-read gate
        // below forces that ordering deterministically: without baseline seeding the
        // loop can only observe the post-change value and the notification is lost.
        var store = new InMemoryStateSource<AppSettings.Fragment>(Fragment("before"));
        var gate = new FirstReadGate<AppSettings.Fragment>(store);
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "store",
                    gate,
                    new StateSourceOptions<AppSettings.Fragment>
                    {
                        Writer = store,
                        Watcher = store,
                    }
                ),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );

        // Establish the watch baseline before subscribing; the seeded baseline keeps
        // the change below observable however the watch-loop startup races.
        (await runtime.GetValueAsync()).Label.ShouldBe("before");

        var observed = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = runtime.OnChange(value =>
            observed.TrySetResult(value.Label)
        );

        // No await between subscription and the external change, and every read
        // past the seeding one is gated: without seeding, the loop inevitably adopts
        // the post-change value as its baseline and this times out.
        store.Set(Fragment("after"));

        (await observed.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe("after");
    }

    /// <summary>
    /// Delays every resolution read after the seeding read so the watch loop cannot
    /// win the startup race. The seed read itself stays fast.
    /// </summary>
    private sealed class FirstReadGate<T>(ISourceReader<T> inner) : ISourceReader<T>
        where T : class
    {
        private int _reads;

        public async ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            if (Interlocked.Increment(ref _reads) >= 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            }

            return await inner.ReadAsync(context, cancellationToken);
        }
    }

    private static AppSettings.Fragment Fragment(string? label) =>
        new() { Label = Optional<string?>.Present(label) };

    private sealed record TestSubject : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From("watcher-lifecycle");
    }

    private sealed class ManualWatcher : ISourceWatcher
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _waiting = new(0);
        private readonly TaskCompletionSource _canceled = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private TaskCompletionSource? _pending;
        private int _waitCount;

        public Task Canceled => _canceled.Task;

        public int WaitCount => Volatile.Read(ref _waitCount);

        public Task WaitUntilWaitingAsync() => _waiting.WaitAsync();

        public async ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            _ = observedRevision;
            TaskCompletionSource pending;
            lock (_gate)
            {
                _pending = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                pending = _pending;
            }

            Interlocked.Increment(ref _waitCount);
            _waiting.Release();
            try
            {
                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                    pending
                );
                await pending.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _canceled.TrySetResult();
                throw;
            }
        }

        public void Signal()
        {
            TaskCompletionSource? pending;
            lock (_gate)
            {
                pending = _pending;
            }

            pending?.TrySetResult();
        }
    }

    private sealed class CountingSubjectState : ISubjectState<AppSettings>
    {
        private int _subscriptions;

        public TaskCompletionSource SecondSubscription { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IWritableState<AppSettings> ForSubject(IConfiglueSubject subject) =>
            new FakeWritableState(this);

        public IConfiglueEditSessions<AppSettings> EditSessionsForSubject(
            IConfiglueSubject subject
        ) => throw new NotSupportedException();

        public void RecordSubscription()
        {
            if (Interlocked.Increment(ref _subscriptions) == 2)
            {
                SecondSubscription.TrySetResult();
            }
        }

        private sealed class FakeWritableState(CountingSubjectState owner)
            : IWritableState<AppSettings>
        {
            public IDisposable OnChange(Action<AppSettings> listener)
            {
                owner.RecordSubscription();
                return new NoopDisposable();
            }

            public ValueTask<AppSettings> GetValueAsync(
                CancellationToken cancellationToken = default
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTaskCompat.FromResult(new AppSettings());
            }

            public ValueTask<StateWriteReceipt> SaveAsync(
                IConfiglueModelPatch<AppSettings> patch,
                CancellationToken cancellationToken = default
            ) => throw new NotSupportedException();
        }
    }

    private sealed class BlockingAccessor : IConfiglueSubjectAccessor, IConfiglueSubjectChangeSource
    {
        private readonly object _gate = new();
        private readonly List<Action> _listeners = [];
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _callCount;

        public Task Entered => _entered.Task;

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<IConfiglueSubject> GetCurrentSubjectAsync(
            CancellationToken cancellationToken = default
        )
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new TestSubject();
        }

        public IDisposable OnChange(Action listener)
        {
            lock (_gate)
            {
                _listeners.Add(listener);
            }

            return new CallbackDisposable(() =>
            {
                lock (_gate)
                {
                    _listeners.Remove(listener);
                }
            });
        }

        public void Release() => _release.TrySetResult();

        public void Invalidate()
        {
            Action[] listeners;
            lock (_gate)
            {
                listeners = [.. _listeners];
            }

            foreach (var listener in listeners)
            {
                listener();
            }
        }
    }

    private sealed class StaticSubjectAccessor : IConfiglueSubjectAccessor
    {
        public ValueTask<IConfiglueSubject> GetCurrentSubjectAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult<IConfiglueSubject>(new TestSubject());
        }
    }

    private sealed class RetryOnceSubjectAccessor : IConfiglueSubjectAccessor
    {
        private int _callCount;

        public TaskCompletionSource FirstFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<IConfiglueSubject> GetCurrentSubjectAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                FirstFailure.TrySetResult();
                throw new InvalidOperationException("Current subject resolution failed once.");
            }

            return ValueTaskCompat.FromResult<IConfiglueSubject>(new TestSubject());
        }
    }

    private sealed class RetryOnceSubjectState : ISubjectState<AppSettings>
    {
        private readonly bool _failFirstBind;
        private int _attemptCount;
        private Action<AppSettings>? _listener;

        public RetryOnceSubjectState(bool failFirstBind = true) => _failFirstBind = failFirstBind;

        public TaskCompletionSource FirstFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Subscribed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public void Raise(AppSettings value) => Volatile.Read(ref _listener)?.Invoke(value);

        public IWritableState<AppSettings> ForSubject(IConfiglueSubject subject) =>
            new RetryOnceWritableState(this);

        public IConfiglueEditSessions<AppSettings> EditSessionsForSubject(
            IConfiglueSubject subject
        ) => throw new NotSupportedException();

        private sealed class RetryOnceWritableState(RetryOnceSubjectState owner)
            : IWritableState<AppSettings>
        {
            public IDisposable OnChange(Action<AppSettings> listener)
            {
                if (Interlocked.Increment(ref owner._attemptCount) == 1 && owner._failFirstBind)
                {
                    owner.FirstFailure.TrySetResult();
                    throw new InvalidOperationException("The initial watcher bind failed.");
                }

                Volatile.Write(ref owner._listener, listener);
                owner.Subscribed.TrySetResult();
                return new NoopDisposable();
            }

            public ValueTask<AppSettings> GetValueAsync(
                CancellationToken cancellationToken = default
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTaskCompat.FromResult(new AppSettings());
            }

            public ValueTask<StateWriteReceipt> SaveAsync(
                IConfiglueModelPatch<AppSettings> patch,
                CancellationToken cancellationToken = default
            ) => throw new NotSupportedException();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
