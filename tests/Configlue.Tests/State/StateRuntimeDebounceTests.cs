using System.Collections.Concurrent;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed partial class StateRuntimeTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Options_DebouncesASourceChangeUntilTheWindowExpires(bool subjectBound)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var watcher = new ManualWatcher();
        var timeProvider = new ObservableTimeProvider();
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new StateSource<AppSettings.Fragment>("user", store, new StateSourceOptions<AppSettings.Fragment> { Watcher = watcher })]),
            onChangeDebounce: TimeSpan.FromMilliseconds(150),
            diagnostics: new ConfiglueRuntimeDiagnosticOptions { EventHistoryCapacity = 64 },
            timeProvider: timeProvider
        );
        var notifications = new ConcurrentQueue<int>();
        var notified = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        void OnChange(AppSettings value)
        {
            notifications.Enqueue(value.RetryCount);
            notified.TrySetResult(value.RetryCount);
        }
        using var subscription = subjectBound
            ? options.ForSubject(new DebounceSubject()).OnChange(OnChange)
            : options.OnChange(OnChange);

        await watcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.ResetTimerRegistration();
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) });
        watcher.Signal();
        await timeProvider.TimerRegistered.WaitAsync(TimeSpan.FromSeconds(5));

        notifications.ShouldBeEmpty();
        timeProvider.Advance(TimeSpan.FromMilliseconds(149));
        notifications.ShouldBeEmpty();
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        (await notified.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(4);
        await watcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        notifications.ToArray().ShouldBe([4]);
        AssertReloadCount(options, 1);
    }

    [Test]
    public async Task Options_CoalescesInvalidationsThatArriveBeforeTheWindowExpires()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var watcher = new ManualWatcher();
        var timeProvider = new ObservableTimeProvider();
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new StateSource<AppSettings.Fragment>("user", store, new StateSourceOptions<AppSettings.Fragment> { Watcher = watcher })]),
            onChangeDebounce: TimeSpan.FromMilliseconds(150),
            diagnostics: new ConfiglueRuntimeDiagnosticOptions { EventHistoryCapacity = 64 },
            timeProvider: timeProvider
        );
        var notifications = new ConcurrentQueue<int>();
        var notified = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = options.OnChange(value =>
        {
            notifications.Enqueue(value.RetryCount);
            notified.TrySetResult(value.RetryCount);
        });

        await watcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.ResetTimerRegistration();
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) });
        watcher.Signal();
        await timeProvider.TimerRegistered.WaitAsync(TimeSpan.FromSeconds(5));

        timeProvider.Advance(TimeSpan.FromMilliseconds(149));
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) });
        watcher.Signal();

        notifications.ShouldBeEmpty();
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        (await notified.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(5);
        await watcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        notifications.ToArray().ShouldBe([5]);
        AssertReloadCount(options, 1);
    }

    [Test]
    public async Task Options_ReloadsAgainForAnInvalidationAfterTheFirstWindow()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var watcher = new ManualWatcher();
        var timeProvider = new ObservableTimeProvider();
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new StateSource<AppSettings.Fragment>("user", store, new StateSourceOptions<AppSettings.Fragment> { Watcher = watcher })]),
            onChangeDebounce: TimeSpan.FromMilliseconds(150),
            diagnostics: new ConfiglueRuntimeDiagnosticOptions { EventHistoryCapacity = 64 },
            timeProvider: timeProvider
        );
        var notifications = new ConcurrentQueue<int>();
        var first = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var second = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = options.OnChange(value =>
        {
            notifications.Enqueue(value.RetryCount);
            if (value.RetryCount == 4)
            {
                first.TrySetResult(value.RetryCount);
            }
            else if (value.RetryCount == 5)
            {
                second.TrySetResult(value.RetryCount);
            }
        });

        await watcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.ResetTimerRegistration();
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) });
        watcher.Signal();
        await timeProvider.TimerRegistered.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromMilliseconds(150));
        (await first.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(4);
        await watcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        timeProvider.ResetTimerRegistration();
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) });
        watcher.Signal();
        await timeProvider.TimerRegistered.WaitAsync(TimeSpan.FromSeconds(5));
        notifications.ToArray().ShouldBe([4]);
        timeProvider.Advance(TimeSpan.FromMilliseconds(150));
        (await second.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(5);
        await watcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        notifications.ToArray().ShouldBe([4, 5]);
        AssertReloadCount(options, 2);
    }

    [Test]
    public async Task Options_DisposalDuringDebounceCancelsWithoutNotifying()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var watcher = new ManualWatcher();
        var timeProvider = new ObservableTimeProvider();
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new StateSource<AppSettings.Fragment>("user", store, new StateSourceOptions<AppSettings.Fragment> { Watcher = watcher })]),
            onChangeDebounce: TimeSpan.FromMilliseconds(150),
            diagnostics: new ConfiglueRuntimeDiagnosticOptions { EventHistoryCapacity = 64 },
            timeProvider: timeProvider
        );
        var notifications = new ConcurrentQueue<int>();
        using var subscription = options.OnChange(value => notifications.Enqueue(value.RetryCount));

        await watcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.ResetTimerRegistration();
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) });
        watcher.Signal();
        await timeProvider.TimerRegistered.WaitAsync(TimeSpan.FromSeconds(5));

        await options.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromMilliseconds(150));

        notifications.ShouldBeEmpty();
        AssertReloadCount(options, 0);
    }

    [Test]
    public async Task Options_DebouncesTopologyInvalidationUsingTheSameWindow()
    {
        var legacy = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var current = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var legacyWatcher = new ManualWatcher();
        var currentWatcher = new ManualWatcher();
        var timeProvider = new ObservableTimeProvider();
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("legacy", legacy, new StateSourceOptions<AppSettings.Fragment> { Priority = 100, Watcher = legacyWatcher }),
                new StateSource<AppSettings.Fragment>("current", current, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = current, Watcher = currentWatcher }),
            ]),
            onChangeDebounce: TimeSpan.FromMilliseconds(150),
            diagnostics: new ConfiglueRuntimeDiagnosticOptions { EventHistoryCapacity = 64 },
            timeProvider: timeProvider
        );
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = ((IConfiglueReloadDiagnostics)options).OnReload(_ =>
            reloaded.TrySetResult()
        );

        await legacyWatcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await currentWatcher.WaitUntilWaitingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.ResetTimerRegistration();
        IConfiglueRuntimeState<AppSettings> writableOptions = options;
        await writableOptions.MigrateSourcesToTargetsAsync(
            [SourceId.From("legacy")],
            new Dictionary<SourceId, Func<IConfiglueFragment, IConfiglueFragment>>()
            {
                [SourceId.From("current")] = static fragment => fragment,
            },
            retireSources: true
        );

        await timeProvider.TimerRegistered.WaitAsync(TimeSpan.FromSeconds(5));
        reloaded.Task.IsCompleted.ShouldBeFalse();
        timeProvider.Advance(TimeSpan.FromMilliseconds(150));
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void AssertReloadCount(
        ConfiglueRuntime<AppSettings, AppSettings.Fragment> options,
        int expected
    )
    {
        options
            .GetRecentEvents()
            .Count(static item => item.Kind == ConfiglueDiagnosticEventKind.ReloadStarted)
            .ShouldBe(expected);
        options
            .GetRecentEvents()
            .Count(static item => item.Kind == ConfiglueDiagnosticEventKind.ReloadCompleted)
            .ShouldBe(expected);
    }

    private sealed class DebounceSubject : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From("debounce-test");
    }

    private sealed class ObservableTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private TaskCompletionSource _timerRegistered = NewSignal();

        public Task TimerRegistered
        {
            get
            {
                lock (_gate)
                {
                    return _timerRegistered.Task;
                }
            }
        }

        public void ResetTimerRegistration()
        {
            lock (_gate)
            {
                _timerRegistered = NewSignal();
            }
        }

        public void Advance(TimeSpan delta)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _utcNow += delta;
                due = _timers.Where(timer => timer.IsDue(_utcNow)).ToArray();
                foreach (var timer in due)
                {
                    _timers.Remove(timer);
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public override long GetTimestamp() => GetUtcNow().Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            _ = period;
            var timer = new ManualTimer(GetUtcNow() + dueTime, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                _timerRegistered.TrySetResult();
            }

            return timer;
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class ManualTimer(
            DateTimeOffset dueAt,
            TimerCallback callback,
            object? state
        ) : ITimer
        {
            private bool _disposed;

            public bool IsDue(DateTimeOffset now) => !_disposed && dueAt <= now;

            public void Fire()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _ = dueTime;
                _ = period;
                return false;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ManualWatcher : ISourceWatcher
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _waiting = new(0);
        private TaskCompletionSource? _pending;

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

            _waiting.Release();
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                pending
            );
            await pending.Task.ConfigureAwait(false);
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
}
