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
        var state = new ControllableSnapshotState<AppSettings>(Snapshot(AppSettingsOf("initial")));
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
        var state = new ControllableSnapshotState<AppSettings>(Snapshot(AppSettingsOf("initial")));
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
        var state = new ControllableSnapshotState<AppSettings>(Snapshot(AppSettingsOf("good")));
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
        var state = new ControllableSnapshotState<AppSettings>(Snapshot(AppSettingsOf("initial")));
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
        var state = new ControllableSnapshotState<AppSettings>(Snapshot(AppSettingsOf("initial")));
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
        ((INotifyPropertyChanged)value).PropertyChanged += (_, args) =>
            changed.Add(args.PropertyName);

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
        ((INotifyPropertyChanged)database).PropertyChanged += (_, args) =>
            changed.Add(args.PropertyName);

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

    [Test]
    public async Task Dispatcher_CancellationCompletesWithoutPumping_AndSkipsQueuedAction()
    {
        var dispatcher = new QueuedDispatcher();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var invocation = dispatcher.InvokeAsync(() => calls++, cancellation.Token);
        invocation.IsCompleted.ShouldBeFalse();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await invocation);
        dispatcher.Pump();
        calls.ShouldBe(0);
    }

    [Test]
    public async Task Dispatcher_CancellationDuringAction_WaitsForActionCompletion()
    {
        var dispatcher = new QueuedDispatcher();
        using var cancellation = new CancellationTokenSource();
        var completedAction = false;
        var invocation = dispatcher.InvokeAsync(
            () =>
            {
                cancellation.Cancel();
                completedAction = true;
            },
            cancellation.Token
        );
        dispatcher.Pump();
        await invocation;
        completedAction.ShouldBeTrue();
    }

    [Test]
    public async Task Reader_ConcurrentInitialize_WaitsForSameSnapshotTransition()
    {
        var state = new GatedSnapshotState<AppSettings>();
        var dispatcher = new QueuedDispatcher();
        using var reader = new ConfiglueStateReader<AppSettings>(state, dispatcher);
        var first = reader.InitializeAsync();
        var second = reader.InitializeAsync();
        state.PendingReads.ShouldBe(1);
        state.ListenerCount.ShouldBe(1);
        first.IsCompleted.ShouldBeFalse();
        second.IsCompleted.ShouldBeFalse();
        state.CompleteOldest(AppSettingsOf("initial"));
        dispatcher.Pump();
        await first;
        await second;
        reader.HasValue.ShouldBeTrue();
    }

    [Test]
    public async Task Editor_ConcurrentInitialize_AndSubjectChangesDuringOpen_AttachNewestOnly()
    {
        var sessions = new GatedEditSessions<AppSettings>();
        var subjects = new FakeSubjectChangeSource();
        using var editor = new ConfiglueStateEditor<AppSettings>(
            sessions,
            ConfiglueDispatcher.Immediate,
            subjectChangeSource: subjects
        );
        var first = editor.InitializeAsync();
        var second = editor.InitializeAsync();
        sessions.PendingCount.ShouldBe(1);
        subjects.ListenerCount.ShouldBe(1);
        subjects.Signal();
        subjects.Signal();
        sessions.PendingCount.ShouldBe(3);
        var newest = CreateUpstreamSession(
            AppSettingsOf("latest"),
            new FakeUpstreamState<AppSettings>(AppSettingsOf("latest"))
        );
        sessions.CompleteNewest(newest);
        editor.Session.ShouldBeSameAs(newest);
        var oldest = CreateUpstreamSession(
            AppSettingsOf("old"),
            new FakeUpstreamState<AppSettings>(AppSettingsOf("old"))
        );
        sessions.CompleteOldest(oldest);
        await first;
        await second;
        var middle = CreateUpstreamSession(
            AppSettingsOf("middle"),
            new FakeUpstreamState<AppSettings>(AppSettingsOf("middle"))
        );
        sessions.CompleteOldest(middle);
        editor.Session.ShouldBeSameAs(newest);
        Should.Throw<InvalidOperationException>(() => oldest.Update(static _ => { }));
        Should.Throw<InvalidOperationException>(() => middle.Update(static _ => { }));
    }

    [Test]
    public async Task Editor_DisposeBeforeQueuedSaveStart_DoesNotUpdateOrNotify()
    {
        var session = CreateUpstreamSession(
            AppSettingsOf("start"),
            new FakeUpstreamState<AppSettings>(AppSettingsOf("start"))
        );
        var dispatcher = new QueuedDispatcher { Access = true };
        var editor = new ConfiglueStateEditor<AppSettings>(
            new StaticEditSessions<AppSettings>(session),
            dispatcher
        );
        await editor.InitializeAsync();
        dispatcher.Access = false;
        var save = editor.SaveAsync();
        var notifications = 0;
        editor.PropertyChanged += (_, _) => notifications++;
        editor.Dispose();
        await Should.ThrowAsync<OperationCanceledException>(async () => await save);
        dispatcher.Pump();
        notifications.ShouldBe(0);
        editor.IsSaving.ShouldBeFalse();
        editor.Session.ShouldBeNull();
    }

    [Test]
    public async Task Reader_InitializeAsync_DoesNotCompleteUntilSnapshotApplied()
    {
        var state = new ControllableSnapshotState<AppSettings>(Snapshot(AppSettingsOf("initial")));
        var dispatcher = new QueuedDispatcher();
        using var reader = new ConfiglueStateReader<AppSettings>(state, dispatcher);

        var initialization = reader.InitializeAsync();

        initialization.IsCompleted.ShouldBeFalse();
        reader.HasValue.ShouldBeFalse();
        reader.IsLoading.ShouldBeTrue();
        dispatcher.PendingCount.ShouldBeGreaterThan(0);

        dispatcher.Pump();
        await initialization;

        reader.HasValue.ShouldBeTrue();
        reader.IsLoading.ShouldBeFalse();
        ((AppSettings.Observable)reader.Value!).Label.ShouldBe("initial");
    }

    [Test]
    public async Task Reader_Reloads_CompleteOutOfOrder_PublishNewestGeneration()
    {
        var state = new GatedSnapshotState<AppSettings>();
        var dispatcher = new QueuedDispatcher();
        using var reader = new ConfiglueStateReader<AppSettings>(state, dispatcher);
        var initialization = reader.InitializeAsync();
        state.CompleteOldest(AppSettingsOf("initial"));
        dispatcher.Pump();
        await initialization;
        ((AppSettings.Observable)reader.Value!).Label.ShouldBe("initial");

        state.Push(AppSettingsOf("first"));
        state.Push(AppSettingsOf("second"));
        state.PendingReads.ShouldBe(2);

        state.CompleteNewest(AppSettingsOf("second"));
        dispatcher.Pump();
        ((AppSettings.Observable)reader.Value!).Label.ShouldBe("second");

        state.CompleteOldest(AppSettingsOf("first"));
        dispatcher.Pump();

        ((AppSettings.Observable)reader.Value!).Label.ShouldBe("second");
    }

    [Test]
    public async Task Editor_SaveAsync_DoesNotCompleteUntilBindableStateUpdated()
    {
        var upstream = new FakeUpstreamState<AppSettings>(AppSettingsOf("start"));
        var session = CreateUpstreamSession(AppSettingsOf("start"), upstream);
        var dispatcher = new QueuedDispatcher { Access = true };
        var editor = new ConfiglueStateEditor<AppSettings>(
            new StaticEditSessions<AppSettings>(session),
            dispatcher
        );
        await editor.InitializeAsync();
        ((AppSettings.Observable)editor.Value!).Label = "saved";
        dispatcher.Access = false;

        var save = editor.SaveAsync();

        save.IsCompleted.ShouldBeFalse();
        editor.LastReceipt.ShouldBeNull();

        await dispatcher.PumpUntilCompletedAsync(save.AsTask());

        editor.IsSaving.ShouldBeFalse();
        editor.IsDirty.ShouldBeFalse();
        editor.LastReceipt.ShouldNotBeNull();
    }

    [Test]
    public async Task Editor_RebaseAsync_DoesNotCompleteUntilBindableStateUpdated()
    {
        var upstream = new FakeUpstreamState<AppSettings>(AppSettingsOf("start"));
        var session = CreateUpstreamSession(AppSettingsOf("start"), upstream);
        var dispatcher = new QueuedDispatcher { Access = true };
        var editor = new ConfiglueStateEditor<AppSettings>(
            new StaticEditSessions<AppSettings>(session),
            dispatcher
        );
        await editor.InitializeAsync();
        ((AppSettings.Observable)editor.Value!).Label = "mine";
        upstream.Push(AppSettingsOf("upstream"));
        editor.HasUpstreamChanges.ShouldBeTrue();
        dispatcher.Access = false;
        var previousProxy = editor.Value;

        var rebase = editor.RebaseAsync();

        rebase.IsCompleted.ShouldBeFalse();
        ReferenceEquals(editor.Value, previousProxy).ShouldBeTrue();

        dispatcher.Pump();
        await rebase;

        editor.HasUpstreamChanges.ShouldBeFalse();
        ReferenceEquals(editor.Value, previousProxy).ShouldBeFalse();
        ((AppSettings.Observable)editor.Value!).Label.ShouldBe("mine");
    }

    [Test]
    public async Task Editor_Dispose_BetweenOpenAndAttach_DoesNotAttachOrLeakSession()
    {
        var upstream = new FakeUpstreamState<AppSettings>(AppSettingsOf("start"));
        var session = CreateUpstreamSession(AppSettingsOf("start"), upstream);
        var dispatcher = new QueuedDispatcher();
        var editor = new ConfiglueStateEditor<AppSettings>(
            new StaticEditSessions<AppSettings>(session),
            dispatcher
        );

        var initialization = editor.InitializeAsync();

        editor.Session.ShouldBeNull();
        dispatcher.PendingCount.ShouldBeGreaterThan(0);

        editor.Dispose();
        await Should.ThrowAsync<OperationCanceledException>(async () => await initialization);
        dispatcher.Pump();

        editor.Session.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => session.Update(static _ => { }));
    }

    [Test]
    public async Task Reader_QueuedCallbackAfterDispose_DoesNotMutateOrNotify()
    {
        var state = new ControllableSnapshotState<AppSettings>(Snapshot(AppSettingsOf("initial")));
        var dispatcher = new QueuedDispatcher { Access = true };
        var reader = new ConfiglueStateReader<AppSettings>(state, dispatcher);
        await reader.InitializeAsync();
        dispatcher.Access = false;

        state.Push(AppSettingsOf("changed"));
        reader.Dispose();
        var notifications = 0;
        reader.PropertyChanged += (_, _) => notifications++;

        dispatcher.Pump();

        notifications.ShouldBe(0);
        reader.HasValue.ShouldBeTrue();
        ((AppSettings.Observable)reader.Value!).Label.ShouldBe("initial");
    }

    [Test]
    public async Task Editor_QueuedCallbackAfterDispose_DoesNotNotify()
    {
        var upstream = new FakeUpstreamState<AppSettings>(AppSettingsOf("start"));
        var session = CreateUpstreamSession(AppSettingsOf("start"), upstream);
        var dispatcher = new QueuedDispatcher { Access = true };
        var editor = new ConfiglueStateEditor<AppSettings>(
            new StaticEditSessions<AppSettings>(session),
            dispatcher
        );
        await editor.InitializeAsync();
        dispatcher.Access = false;

        upstream.Push(AppSettingsOf("upstream"));
        editor.Dispose();
        var notifications = 0;
        editor.PropertyChanged += (_, _) => notifications++;

        dispatcher.Pump();

        notifications.ShouldBe(0);
    }

    [Test]
    public async Task Reader_DispatcherRejection_SurfacesFromInitializeAsync()
    {
        var state = new ControllableSnapshotState<AppSettings>(Snapshot(AppSettingsOf("initial")));
        var dispatcher = new QueuedDispatcher
        {
            Reject = true,
            RejectionError = new InvalidOperationException("The dispatcher is shutting down."),
        };
        using var reader = new ConfiglueStateReader<AppSettings>(state, dispatcher);

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await reader.InitializeAsync()
        );

        exception.Message.ShouldBe("The dispatcher is shutting down.");
        reader.HasValue.ShouldBeFalse();
    }

    [Test]
    public async Task Editor_SaveAsync_DispatcherRejection_Surfaces()
    {
        var upstream = new FakeUpstreamState<AppSettings>(AppSettingsOf("start"));
        var session = CreateUpstreamSession(AppSettingsOf("start"), upstream);
        var dispatcher = new QueuedDispatcher
        {
            Access = true,
            RejectionError = new InvalidOperationException("The dispatcher is shutting down."),
        };
        var editor = new ConfiglueStateEditor<AppSettings>(
            new StaticEditSessions<AppSettings>(session),
            dispatcher
        );
        await editor.InitializeAsync();
        ((AppSettings.Observable)editor.Value!).Label = "saved";
        dispatcher.Access = false;
        dispatcher.Reject = true;

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await editor.SaveAsync()
        );

        exception.Message.ShouldBe("The dispatcher is shutting down.");
        editor.IsSaving.ShouldBeFalse();
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
            WrapSave(save),
            static (baseline, desired, current) =>
                string.Equals(desired.Label, baseline.Label, StringComparison.Ordinal)
                    ? current
                    : desired,
            static (current, baseline) => !AppSettings.Fragment.Diff(baseline, current).IsEmpty,
            _ => ValueTask.FromResult(new StateSnapshot<AppSettings>(upstream.Current, null)),
            static value => value.DeepClone(),
            defaultValue ?? AppSettingsOf("default"),
            upstream
        );

    private static Func<
        AppSettings,
        CancellationToken,
        ValueTask<StateCommitResult<AppSettings>>
    > WrapSave(Func<AppSettings, CancellationToken, ValueTask<StateWriteReceipt>>? save) =>
        save is null
            ? static (value, _) =>
                ValueTask.FromResult(
                    new StateCommitResult<AppSettings>(
                        StateWriteReceipt.Empty,
                        new StateSnapshot<AppSettings>(value, null)
                    )
                )
            : async (value, token) =>
                new StateCommitResult<AppSettings>(
                    await save(value, token).ConfigureAwait(false),
                    new StateSnapshot<AppSettings>(value, null)
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

        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Access)
            {
                action();
                return ValueTask.CompletedTask;
            }

            var completion = new TaskCompletionSource<bool>();
            _pending.Enqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            return new ValueTask(completion.Task);
        }

        public void Pump()
        {
            while (_pending.Count > 0)
            {
                _pending.Dequeue()();
            }
        }
    }

    private sealed class QueuedDispatcher : IConfiglueDispatcher
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _pending = new();
        private TaskCompletionSource<bool> _workAvailable = NewSignal();
        public bool Access { get; set; }
        public bool Reject { get; set; }
        public Exception? RejectionError { get; set; }
        public int PendingCount
        {
            get
            {
                lock (_gate)
                    return _pending.Count;
            }
        }

        public bool CheckAccess() => Access;

        public void Post(Action action)
        {
            if (Reject)
                throw RejectionError
                    ?? new InvalidOperationException("The dispatcher rejected the callback.");
            lock (_gate)
            {
                _pending.Enqueue(action);
                var signal = _workAvailable;
                _workAvailable = NewSignal();
                signal.TrySetResult(true);
            }
        }

        public ValueTask InvokeAsync(
            Action action,
            CancellationToken cancellationToken = default
        ) => ConfiglueDispatcher.InvokeAsync(this, action, cancellationToken);

        public void Pump()
        {
            while (true)
            {
                Action action;
                lock (_gate)
                {
                    if (_pending.Count == 0)
                        return;
                    action = _pending.Dequeue();
                }
                action();
            }
        }

        public async Task PumpUntilCompletedAsync(Task completion)
        {
            while (!completion.IsCompleted)
            {
                Task signal;
                lock (_gate)
                    signal = _workAvailable.Task;
                Pump();
                await Task.WhenAny(completion, signal);
            }
            await completion;
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatedSnapshotState<T>
        : IReadOnlyState<T>,
            IConfiglueStateSnapshotRuntime<T>
    {
        private readonly List<Action<T>> _listeners = [];
        private readonly List<TaskCompletionSource<StateSnapshot<T>>> _reads = [];
        private T? _value;

        public int ListenerCount => _listeners.Count;

        public int PendingReads => _reads.Count;

        public IDisposable OnChange(Action<T> listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            _listeners.Add(listener);
            return new ActionDisposable(() => _listeners.Remove(listener));
        }

        public ValueTask<T> GetValueAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_value!);
        }

        public ValueTask<StateSnapshot<T>> GetSnapshotAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<StateSnapshot<T>>();
            _reads.Add(completion);
            return new ValueTask<StateSnapshot<T>>(completion.Task);
        }

        public void Push(T value)
        {
            foreach (var listener in _listeners.ToArray())
            {
                listener(value);
            }
        }

        public void CompleteNewest(T value)
        {
            var index = _reads.Count - 1;
            var completion = _reads[index];
            _reads.RemoveAt(index);
            _value = value;
            completion.SetResult(new StateSnapshot<T>(value, null));
        }

        public void CompleteOldest(T value)
        {
            var completion = _reads[0];
            _reads.RemoveAt(0);
            _value = value;
            completion.SetResult(new StateSnapshot<T>(value, null));
        }
    }

    private sealed class ControllableSnapshotState<T>
        : IReadOnlyState<T>,
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

    private sealed class GatedEditSessions<T> : IConfiglueEditSessions<T>
        where T : class
    {
        private readonly List<TaskCompletionSource<EditSession<T>>> _pending = [];
        public int PendingCount => _pending.Count;

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            CancellationToken cancellationToken = default
        )
        {
            var completion = new TaskCompletionSource<EditSession<T>>();
            _pending.Add(completion);
            return new ValueTask<EditSession<T>>(completion.Task);
        }

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            StateWritePlan writePlan,
            CancellationToken cancellationToken = default
        ) => OpenEditSessionAsync(cancellationToken);

        public void CompleteNewest(EditSession<T> session) => Complete(_pending.Count - 1, session);

        public void CompleteOldest(EditSession<T> session) => Complete(0, session);

        private void Complete(int index, EditSession<T> session)
        {
            var completion = _pending[index];
            _pending.RemoveAt(index);
            completion.SetResult(session);
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
