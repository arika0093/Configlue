using System.ComponentModel;
using Configlue;
using Configlue.Extensions.ComponentModel;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed class ComponentModelAdapterTests
{
    [Test]
    public async Task Reader_InitialLoad_ResolvesSnapshotAndBindableValue()
    {
        var state = new ControllableSnapshotState<AppSettings>(
            Snapshot(AppSettingsOf("initial"))
        );
        using var reader = new ConfiglueStateReader<AppSettings>(
            state,
            ConfiglueDispatcher.Immediate
        );

        await reader.InitializeAsync();

        reader.HasValue.ShouldBeTrue();
        reader.IsLoading.ShouldBeFalse();
        reader.LoadFailure.ShouldBeNull();
        reader.Value.ShouldBeOfType<AppSettings.Observable>();
        ((AppSettings.Observable)reader.Value!).Label.ShouldBe("initial");
    }

    [Test]
    public async Task Reader_Change_RefreshesBindableValue()
    {
        var state = new ControllableSnapshotState<AppSettings>(
            Snapshot(AppSettingsOf("initial"))
        );
        using var reader = new ConfiglueStateReader<AppSettings>(
            state,
            ConfiglueDispatcher.Immediate
        );
        await reader.InitializeAsync();

        state.Push(AppSettingsOf("changed"));

        await WaitUntilAsync(() => ((AppSettings.Observable)reader.Value!).Label == "changed");
    }

    [Test]
    public async Task Reader_ReloadFailure_PreservesLastKnownGoodValue()
    {
        var state = new ControllableSnapshotState<AppSettings>(
            Snapshot(AppSettingsOf("good"))
        );
        using var reader = new ConfiglueStateReader<AppSettings>(
            state,
            ConfiglueDispatcher.Immediate
        );
        await reader.InitializeAsync();

        state.FailWith = new InvalidOperationException("reload failed");
        state.Push(AppSettingsOf("bad"));

        await WaitUntilAsync(() => reader.ReloadFailure is not null);
        ((AppSettings.Observable)reader.Value!).Label.ShouldBe("good");
        reader.ReloadFailure!.Message.ShouldBe("reload failed");
        reader.LoadFailure.ShouldBeNull();
    }

    [Test]
    public async Task Reader_InitialFailure_IsSurfacedAsLoadFailure()
    {
        var state = new ControllableSnapshotState<AppSettings>(Snapshot(AppSettingsOf("x")))
        {
            FailWith = new InvalidOperationException("cannot read"),
        };
        using var reader = new ConfiglueStateReader<AppSettings>(
            state,
            ConfiglueDispatcher.Immediate
        );

        await reader.InitializeAsync();

        reader.LoadFailure!.Message.ShouldBe("cannot read");
        reader.HasValue.ShouldBeFalse();
    }

    [Test]
    public async Task Reader_MarshalsNotificationsThroughDispatcher()
    {
        var state = new ControllableSnapshotState<AppSettings>(
            Snapshot(AppSettingsOf("initial"))
        );
        var dispatcher = new ManualDispatcher();
        using var reader = new ConfiglueStateReader<AppSettings>(state, dispatcher);
        await reader.InitializeAsync();

        dispatcher.Access = false;
        state.Push(AppSettingsOf("changed"));

        dispatcher.PendingCount.ShouldBeGreaterThan(0);
        ((AppSettings.Observable)reader.Value!).Label.ShouldBe("initial");

        dispatcher.Access = true;
        dispatcher.Pump();

        ((AppSettings.Observable)reader.Value!).Label.ShouldBe("changed");
    }

    [Test]
    public async Task Reader_Dispose_UnsubscribesFromState()
    {
        var state = new ControllableSnapshotState<AppSettings>(
            Snapshot(AppSettingsOf("initial"))
        );
        var reader = new ConfiglueStateReader<AppSettings>(state, ConfiglueDispatcher.Immediate);
        await reader.InitializeAsync();
        state.ListenerCount.ShouldBe(1);

        reader.Dispose();

        state.ListenerCount.ShouldBe(0);
    }

    [Test]
    public async Task Editor_OpenedFromRuntime_ExposesSessionAndDetails()
    {
        using var provider = BuildProvider("initial");
        var editor = new ConfiglueStateEditor<AppSettings>(
            provider.GetRequiredService<IConfiglueEditSessions<AppSettings>>(),
            ConfiglueDispatcher.Immediate
        );

        await editor.InitializeAsync();

        editor.IsLoading.ShouldBeFalse();
        editor.Session.ShouldNotBeNull();
        editor.SessionStart.ShouldNotBeNull();
        var details = editor.SessionStart!.GetDetails();
        details.Label.Value.ShouldBe("initial");
        details.RetryCount.IsEditable.ShouldBeTrue();
        ((AppSettings.Observable)editor.Value!).Label.ShouldBe("initial");
    }

    [Test]
    public async Task Editor_RootEdit_RaisesNotificationAndMarksDirty()
    {
        var upstream = new FakeUpstreamState<AppSettings>(AppSettingsOf("start"));
        var editor = CreateEditor(CreateUpstreamSession(AppSettingsOf("start"), upstream));
        await editor.InitializeAsync();
        var value = (AppSettings.Observable)editor.Value!;
        var changed = new List<string?>();
        ((INotifyPropertyChanged)value).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        value.Label = "edited";

        editor.IsDirty.ShouldBeTrue();
        changed.ShouldContain("Label");
    }

    [Test]
    public async Task Editor_NestedEdit_IsObservedAndMarksDirty()
    {
        var upstream = new FakeUpstreamState<AppSettings>(AppSettingsOf("start"));
        var editor = CreateEditor(CreateUpstreamSession(AppSettingsOf("start"), upstream));
        await editor.InitializeAsync();
        var value = (AppSettings.Observable)editor.Value!;
        var database = value.Database!;
        var changed = new List<string?>();
        ((INotifyPropertyChanged)database).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        database.Host = "db.example.com";

        changed.ShouldContain("Host");
        editor.IsDirty.ShouldBeTrue();
        editor.Session!.Value.Database!.Host.ShouldBe("db.example.com");
    }

    [Test]
    public async Task Editor_ValidationFailure_IsSurfacedThroughNotifyDataErrorInfo()
    {
        var session = new EditSession<AppSettings>(
            AppSettingsOf("start"),
            (_, _) =>
                throw new ConfiglueValidationException(
                    "AppSettings",
                    typeof(AppSettings),
                    ["RetryCount must be between 0 and 100."]
                )
        );
        var editor = CreateEditor(session);
        await editor.InitializeAsync();
        var errorsChanged = 0;
        ((INotifyDataErrorInfo)editor).ErrorsChanged += (_, _) => errorsChanged++;

        await editor.SaveAsync();

        editor.HasErrors.ShouldBeTrue();
        editor.ValidationFailures.ShouldContain("RetryCount must be between 0 and 100.");
        ((INotifyDataErrorInfo)editor)
            .GetErrors(string.Empty)
            .Cast<string>()
            .ShouldContain("RetryCount must be between 0 and 100.");
        ((IDataErrorInfo)editor).Error.ShouldContain("RetryCount");
        errorsChanged.ShouldBeGreaterThan(0);

        editor.ResetToSessionStart();
        editor.HasErrors.ShouldBeFalse();
    }

    [Test]
    public async Task Editor_CleanUpstreamChange_AutoRebases()
    {
        var upstream = new FakeUpstreamState<AppSettings>(AppSettingsOf("start"));
        var editor = CreateEditor(CreateUpstreamSession(AppSettingsOf("start"), upstream));
        await editor.InitializeAsync();

        upstream.Push(AppSettingsOf("upstream"));

        await WaitUntilAsync(() => ((AppSettings.Observable)editor.Value!).Label == "upstream");
        editor.IsDirty.ShouldBeFalse();
        editor.HasUpstreamChanges.ShouldBeFalse();
    }

    [Test]
    public async Task Editor_DirtyUpstreamChange_PreservesDraftAndReportsUpstream()
    {
        var upstream = new FakeUpstreamState<AppSettings>(AppSettingsOf("start"));
        var editor = CreateEditor(CreateUpstreamSession(AppSettingsOf("start"), upstream));
        await editor.InitializeAsync();
        ((AppSettings.Observable)editor.Value!).Label = "mine";

        upstream.Push(AppSettingsOf("upstream"));

        await WaitUntilAsync(() => editor.HasUpstreamChanges);
        ((AppSettings.Observable)editor.Value!).Label.ShouldBe("mine");
        editor.IsDirty.ShouldBeTrue();
    }

    [Test]
    public async Task Editor_Resets_UseCoreSemantics()
    {
        var upstream = new FakeUpstreamState<AppSettings>(AppSettingsOf("start"));
        var editor = CreateEditor(
            CreateUpstreamSession(
                AppSettingsOf("start"),
                upstream,
                defaultValue: AppSettingsOf("default")
            )
        );
        await editor.InitializeAsync();
        ((AppSettings.Observable)editor.Value!).Label = "mine";
        upstream.Push(AppSettingsOf("upstream"));
        await WaitUntilAsync(() => editor.HasUpstreamChanges);

        editor.ResetToUpstream();
        ((AppSettings.Observable)editor.Value!).Label.ShouldBe("upstream");

        ((AppSettings.Observable)editor.Value!).Label = "other";
        editor.ResetToSessionStart();
        ((AppSettings.Observable)editor.Value!).Label.ShouldBe("start");

        editor.ResetToDefault();
        ((AppSettings.Observable)editor.Value!).Label.ShouldBe("default");
    }

    [Test]
    public async Task Editor_SubjectChange_DirtyPreservesAndBlocksSave()
    {
        var subject = new FakeSubjectChangeSource();
        var saveCalls = 0;
        var session = CreateUpstreamSession(
            AppSettingsOf("subject-a"),
            new FakeUpstreamState<AppSettings>(AppSettingsOf("subject-a")),
            save: (_, _) =>
            {
                Interlocked.Increment(ref saveCalls);
                return ValueTask.FromResult(StateWriteReceipt.Empty);
            }
        );
        var editor = new ConfiglueStateEditor<AppSettings>(
            new StaticEditSessions<AppSettings>(session),
            ConfiglueDispatcher.Immediate,
            subjectChangeSource: subject
        );
        await editor.InitializeAsync();
        ((AppSettings.Observable)editor.Value!).Label = "dirty-a";

        subject.Signal();
        await WaitUntilAsync(() => editor.IsSubjectChanged);

        await editor.SaveAsync();

        saveCalls.ShouldBe(0);
        editor.LastException.ShouldNotBeNull();
        ((AppSettings.Observable)editor.Value!).Label.ShouldBe("dirty-a");
        subject.ListenerCount.ShouldBe(1);

        editor.Dispose();
        subject.ListenerCount.ShouldBe(0);
    }

    [Test]
    public async Task Editor_SubjectChange_CleanEditorReopens()
    {
        var subject = new FakeSubjectChangeSource();
        var sessions = new SwitchingEditSessions<AppSettings>(() =>
            CreateUpstreamSession(
                AppSettingsOf("subject-a"),
                new FakeUpstreamState<AppSettings>(AppSettingsOf("subject-a"))
            )
        );
        var editor = new ConfiglueStateEditor<AppSettings>(
            sessions,
            ConfiglueDispatcher.Immediate,
            subjectChangeSource: subject
        );
        await editor.InitializeAsync();

        sessions.Current = () =>
            CreateUpstreamSession(
                AppSettingsOf("subject-b"),
                new FakeUpstreamState<AppSettings>(AppSettingsOf("subject-b"))
            );
        subject.Signal();

        await WaitUntilAsync(() => ((AppSettings.Observable)editor.Value!).Label == "subject-b");
        editor.IsSubjectChanged.ShouldBeFalse();
    }

    [Test]
    public async Task Editor_SaveSuccess_ClearsErrorsAndCommits()
    {
        var committed = 0;
        var upstream = new FakeUpstreamState<AppSettings>(AppSettingsOf("start"));
        var session = CreateUpstreamSession(
            AppSettingsOf("start"),
            upstream,
            save: (_, _) =>
            {
                committed++;
                return ValueTask.FromResult(StateWriteReceipt.Empty);
            }
        );
        var editor = CreateEditor(session);
        await editor.InitializeAsync();
        ((AppSettings.Observable)editor.Value!).Label = "saved";

        await editor.SaveAsync();

        committed.ShouldBe(1);
        editor.IsSaving.ShouldBeFalse();
        editor.IsDirty.ShouldBeFalse();
        editor.LastReceipt.ShouldNotBeNull();
        editor.HasErrors.ShouldBeFalse();
    }

    private static AppSettings AppSettingsOf(string label) =>
        new()
        {
            Label = label,
            RetryCount = 3,
            Database = new DatabaseSettings { Host = "localhost", Port = 5432 },
        };

    private static StateSnapshot<AppSettings> Snapshot(AppSettings value) =>
        new(value.DeepClone(), null);

    private static ConfiglueStateEditor<AppSettings> CreateEditor(
        EditSession<AppSettings> session
    ) => new(new StaticEditSessions<AppSettings>(session), ConfiglueDispatcher.Immediate);

    private static EditSession<AppSettings> CreateUpstreamSession(
        AppSettings value,
        FakeUpstreamState<AppSettings> upstream,
        AppSettings? defaultValue = null,
        Func<AppSettings, CancellationToken, ValueTask<StateWriteReceipt>>? save = null
    ) =>
        new(
            value.DeepClone(),
            Snapshot(value),
            save ?? (static (_, _) => ValueTask.FromResult(StateWriteReceipt.Empty)),
            static (baseline, desired, current) =>
                string.Equals(desired.Label, baseline.Label, StringComparison.Ordinal)
                    ? current
                    : desired,
            static (current, baseline) =>
                !AppSettings.Fragment.Diff(baseline, current).IsEmpty,
            _ => ValueTask.FromResult(new StateSnapshot<AppSettings>(upstream.Current, null)),
            static value => value.DeepClone(),
            defaultValue ?? AppSettingsOf("default"),
            upstream
        );

    private static ServiceProvider BuildProvider(string label)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Label = Optional<string?>.Present(label),
                RetryCount = Optional<int>.Present(3),
            }
        );
        var services = new ServiceCollection();
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("settings", store, writer: store, watcher: store),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        return services.BuildServiceProvider();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The expected condition was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ManualDispatcher : IConfiglueDispatcher
    {
        private readonly Queue<Action> _pending = new();

        public bool Access { get; set; } = true;

        public int PendingCount => _pending.Count;

        public bool CheckAccess() => Access;

        public void Post(Action action) => _pending.Enqueue(action);

        public void Pump()
        {
            while (_pending.Count > 0)
            {
                _pending.Dequeue()();
            }
        }
    }

    private sealed class ControllableSnapshotState<T> :
        IReadOnlyState<T>,
        IConfiglueStateSnapshotRuntime<T>
    {
        private readonly List<Action<T>> _listeners = [];
        private StateSnapshot<T> _snapshot;

        public ControllableSnapshotState(StateSnapshot<T> snapshot) => _snapshot = snapshot;

        public Exception? FailWith { get; set; }

        public int ListenerCount => _listeners.Count;

        public IDisposable OnChange(Action<T> listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            _listeners.Add(listener);
            return new ActionDisposable(() => _listeners.Remove(listener));
        }

        public ValueTask<T> GetValueAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_snapshot.Value);

        public ValueTask<StateSnapshot<T>> GetSnapshotAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWith is not null)
            {
                throw FailWith;
            }

            return ValueTask.FromResult(_snapshot);
        }

        public void Push(T value)
        {
            _snapshot = new StateSnapshot<T>(value, null);
            foreach (var listener in _listeners.ToArray())
            {
                listener(value);
            }
        }
    }

    private sealed class FakeUpstreamState<T> : IReadOnlyState<T>
    {
        private Action<T>? _listener;

        public FakeUpstreamState(T current) => Current = current;

        public T Current { get; private set; }

        public IDisposable OnChange(Action<T> listener)
        {
            _listener = listener;
            return new ActionDisposable(() => _listener = null);
        }

        public ValueTask<T> GetValueAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);

        public void Push(T value)
        {
            Current = value;
            _listener?.Invoke(value);
        }
    }

    private sealed class FakeSubjectChangeSource : IConfiglueSubjectChangeSource
    {
        private readonly List<Action> _listeners = [];

        public int ListenerCount => _listeners.Count;

        public IDisposable OnChange(Action listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            _listeners.Add(listener);
            return new ActionDisposable(() => _listeners.Remove(listener));
        }

        public void Signal()
        {
            foreach (var listener in _listeners.ToArray())
            {
                listener();
            }
        }
    }

    private sealed class StaticEditSessions<T> : IConfiglueEditSessions<T>
        where T : class
    {
        private readonly EditSession<T> _session;

        public StaticEditSessions(EditSession<T> session) => _session = session;

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(_session);

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            StateWritePlan writePlan,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(writePlan);
            return ValueTask.FromResult(_session);
        }
    }

    private sealed class SwitchingEditSessions<T> : IConfiglueEditSessions<T>
        where T : class
    {
        public SwitchingEditSessions(Func<EditSession<T>> current) => Current = current;

        public Func<EditSession<T>> Current { get; set; }

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(Current());

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            StateWritePlan writePlan,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(writePlan);
            return ValueTask.FromResult(Current());
        }
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
