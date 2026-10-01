using System.Collections.Concurrent;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class EditSessionSnapshotTests
{
    [Test]
    public async Task GetSnapshotAsync_ResolvesValueAndDetailsFromOneRead()
    {
        var store = new SignalingStore<AppSettings.Fragment>(
            Fragment("snapshot", retryCount: 4)
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("counted", store)])
        );

        var snapshot = await ((IReadOnlyState<AppSettings>)options).GetSnapshotAsync();
        var details = snapshot.GetDetails();

        (snapshot.Value!.Label).ShouldBe("snapshot");
        (details.RetryCount.Value).ShouldBe(4);
        (details.Label.Value).ShouldBe("snapshot");
        (details.RetryCount.Source?.Key).ShouldBe(details.RetryCount.Sources[0].Source.Key);
        (store.ReadCount).ShouldBe(1);
    }

    [Test]
    public async Task GetSnapshotAsync_ThrowsForStateWithoutSnapshotSupport()
    {
        IReadOnlyState<AppSettings> state = new UnsupportedState();

        await Should.ThrowAsync<NotSupportedException>(async () =>
            await state.GetSnapshotAsync()
        );
    }

    [Test]
    public async Task EditSession_SessionStartDetailsMatchIndependentDetails()
    {
        var policy = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("policy") }
        );
        var user = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("policy", policy, priority: 100),
                new("user", user, priority: 0, writer: user),
            ]),
            StateWritePlan.DefaultTo("user")
        );

        var expected = await options.GetDetailsAsync();
        using var session = await options.OpenEditSessionAsync();
        var actual = session.SessionStart.GetDetails();

        (actual.RetryCount.Value).ShouldBe(expected.RetryCount.Value);
        (actual.RetryCount.Source?.Kind).ShouldBe(expected.RetryCount.Source?.Kind);
        (actual.RetryCount.Editability).ShouldBe(expected.RetryCount.Editability);
        (actual.Label.Value).ShouldBe(expected.Label.Value);
        (actual.Label.Editability).ShouldBe(expected.Label.Editability);
        (actual.Label.Editability).ShouldBe(ConfiglueEditability.Shadowed);
        (actual.RetryCount.Editability).ShouldBe(ConfiglueEditability.Editable);
        (session.SessionStart.Details).ShouldNotBeNull();
    }

    [Test]
    public async Task EditSessionsForSubject_ReadsRebasesAndCommitsToTheFixedSubject()
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var subjectA = new SettingsSubject("tenant-a", "user-a");
        var subjectB = new SettingsSubject("tenant-b", "user-b");
        users.Set(subjectA.Key, Fragment("a", retryCount: 3));
        users.Set(subjectB.Key, Fragment("b", retryCount: 5));
        await using var runtime = CreateSubjectRuntime(users);
        ISubjectState<AppSettings> subjectState = runtime;

        var sessions = subjectState.EditSessionsForSubject(subjectA);
        using (var session = await sessions.OpenEditSessionAsync())
        {
            (session.Value.Label).ShouldBe("a");
            session.Value.Label = "a-edited";
            await session.CommitAsync();
        }

        (users.Read(subjectA.Key).Value!.Label.Value).ShouldBe("a-edited");
        (users.Read(subjectB.Key).Value!.Label.Value).ShouldBe("b");

        using (var session = await sessions.OpenEditSessionAsync())
        {
            (session.Value.Label).ShouldBe("a-edited");
            session.Value.RetryCount = 7;
            users.Set(subjectA.Key, Fragment("a-external", retryCount: 3));
            await session.RebaseAsync();

            (session.Value.RetryCount).ShouldBe(7);
            (session.Value.Label).ShouldBe("a-external");
            await session.CommitAsync();
        }

        var stored = users.Read(subjectA.Key).Value!;
        (stored.Label.Value).ShouldBe("a-external");
        (stored.RetryCount.Value).ShouldBe(7);
        (users.Read(subjectB.Key).Value!.Label.Value).ShouldBe("b");
    }

    [Test]
    public async Task CurrentSubjectEditSession_DoesNotRetargetAfterTheSubjectChanges()
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var subjectA = new SettingsSubject("tenant-a", "user-a");
        var subjectB = new SettingsSubject("tenant-b", "user-b");
        users.Set(subjectA.Key, Fragment("a"));
        users.Set(subjectB.Key, Fragment("b"));
        await using var runtime = CreateSubjectRuntime(users);
        var accessor = new MutableSubjectAccessor(subjectA);
        var current = new CurrentSubjectState<AppSettings>(runtime, accessor);
        IConfiglueEditSessions<AppSettings> sessions = current;

        using var session = await sessions.OpenEditSessionAsync();
        accessor.Set(subjectB);
        session.Value.Label = "edited";
        await session.CommitAsync();

        var snapshot = await (
            (IConfiglueStateSnapshotRuntime<AppSettings>)current
        ).GetSnapshotAsync();

        (snapshot.Value!.Label).ShouldBe("b");
        (users.Read(subjectA.Key).Value!.Label.Value).ShouldBe("edited");
        (users.Read(subjectB.Key).Value!.Label.Value).ShouldBe("b");
    }

    [Test]
    public async Task UpstreamChange_MarksUpstreamWithoutOverwritingADirtyDraft()
    {
        var store = new SignalingStore<AppSettings.Fragment>(Fragment("start", retryCount: 3));
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", store, writer: store, watcher: store),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        using var session = await options.OpenEditSessionAsync();
        await store.WatchStarted.WaitAsync(TimeSpan.FromSeconds(5));

        session.Value.RetryCount = 6;
        store.Set(Fragment("start", retryCount: 3, enabled: false));
        await WaitForUpstreamChangesAsync(session);

        (session.HasUpstreamChanges).ShouldBeTrue();
        (session.HasLocalChanges).ShouldBeTrue();
        (session.Value.RetryCount).ShouldBe(6);
        (session.Value.Enabled).ShouldBeTrue();
        (session.LatestUpstream!.Value!.Enabled).ShouldBeFalse();
    }

    [Test]
    public async Task RebaseAsync_AdoptsUpstreamForACleanSession()
    {
        var store = new SignalingStore<AppSettings.Fragment>(Fragment("start", retryCount: 3));
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", store, writer: store, watcher: store),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        using var session = await options.OpenEditSessionAsync();

        store.Set(Fragment("external", retryCount: 9));
        await session.RebaseAsync();

        (session.Value.Label).ShouldBe("external");
        (session.Value.RetryCount).ShouldBe(9);
        (session.HasLocalChanges).ShouldBeFalse();
        (session.HasUpstreamChanges).ShouldBeFalse();
    }

    [Test]
    public async Task RebaseAsync_ReappliesDirtyDraftOntoUpstream()
    {
        var store = new SignalingStore<AppSettings.Fragment>(Fragment("start", retryCount: 3));
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", store, writer: store, watcher: store),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        using var session = await options.OpenEditSessionAsync();

        session.Value.RetryCount = 6;
        store.Set(Fragment("start", retryCount: 3, enabled: false));
        await session.RebaseAsync();

        (session.Value.RetryCount).ShouldBe(6);
        (session.Value.Enabled).ShouldBeFalse();
        (session.HasLocalChanges).ShouldBeTrue();
        (session.HasUpstreamChanges).ShouldBeFalse();

        await session.CommitAsync();
        var stored = (await store.ReadAsync()).Value!;
        (stored.RetryCount.Value).ShouldBe(6);
        (stored.Enabled.Value).ShouldBeFalse();
    }

    [Test]
    public async Task RebaseAsync_ThrowsWhenTheSameMemberChangedUpstream()
    {
        var store = new SignalingStore<AppSettings.Fragment>(Fragment("start", retryCount: 3));
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", store, writer: store, watcher: store),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        using var session = await options.OpenEditSessionAsync();

        session.Value.RetryCount = 6;
        store.Set(Fragment("start", retryCount: 9));

        var conflict = await Should.ThrowAsync<StateConflictException>(async () =>
            await session.RebaseAsync()
        );
        conflict.Message.ShouldContain("RetryCount");
        (session.Value.RetryCount).ShouldBe(6);
    }

    [Test]
    public async Task ResetToUpstream_PerformsNoIo()
    {
        var store = new SignalingStore<AppSettings.Fragment>(Fragment("start", retryCount: 3));
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", store, writer: store, watcher: store),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        using var session = await options.OpenEditSessionAsync();

        store.Set(Fragment("external", retryCount: 9));
        await session.RebaseAsync();
        session.Value.RetryCount = 4;
        var revisionBefore = (await store.ReadAsync()).Revision;
        var readsBefore = store.ReadCount;

        session.ResetToUpstream();

        (session.Value.RetryCount).ShouldBe(9);
        (session.Value.Label).ShouldBe("external");
        (session.HasLocalChanges).ShouldBeFalse();
        (session.HasUpstreamChanges).ShouldBeFalse();
        (store.ReadCount).ShouldBe(readsBefore);
        ((await store.ReadAsync()).Revision).ShouldBe(revisionBefore);
    }

    [Test]
    public async Task ResetToSessionStart_IsDistinctFromResetToUpstream()
    {
        var store = new SignalingStore<AppSettings.Fragment>(Fragment("start", retryCount: 3));
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", store, writer: store, watcher: store),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        using var session = await options.OpenEditSessionAsync();

        session.Value.RetryCount = 6;
        store.Set(Fragment("start", retryCount: 3, enabled: false));
        await session.RebaseAsync();

        session.ResetToSessionStart();
        (session.Value.RetryCount).ShouldBe(3);
        (session.Value.Enabled).ShouldBeTrue();

        session.ResetToUpstream();
        (session.Value.RetryCount).ShouldBe(3);
        (session.Value.Enabled).ShouldBeFalse();
    }

    [Test]
    public async Task Dispose_UnsubscribesUpstreamListeners()
    {
        var store = new SignalingStore<AppSettings.Fragment>(Fragment("start", retryCount: 3));
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", store, writer: store, watcher: store),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        var session = await options.OpenEditSessionAsync();
        await store.WatchStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var notified = 0;
        session.UpstreamChanged += () => Interlocked.Increment(ref notified);

        session.Dispose();
        store.Set(Fragment("external", retryCount: 9));
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        (notified).ShouldBe(0);
        (session.HasUpstreamChanges).ShouldBeFalse();
    }

    [Test]
    public async Task Dispose_AllowsAnInFlightSaveToFinish()
    {
        var saveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Func<int, CancellationToken, ValueTask<StateWriteReceipt>> save = async (_, _) =>
        {
            saveStarted.TrySetResult();
            await releaseSave.Task;
            return StateWriteReceipt.Empty;
        };
        var session = new EditSession<int>(5, save);

        var commit = session.CommitAsync().AsTask();
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Dispose();
        releaseSave.TrySetResult();
        await commit.WaitAsync(TimeSpan.FromSeconds(5));

        (session.IsCommitted).ShouldBeTrue();
    }

    [Test]
    public void LegacySession_HasNoUpstreamSnapshot()
    {
        using var session = new EditSession<int>(
            5,
            (_, _) => ValueTaskCompat.FromResult(StateWriteReceipt.Empty)
        );

        (session.SessionStart.Value).ShouldBe(5);
        (session.LatestUpstream).ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => session.ResetToUpstream());

        session.ResetToSessionStart();
        (session.Value).ShouldBe(5);
    }

    private static async Task WaitForUpstreamChangesAsync<T>(EditSession<T> session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!session.HasUpstreamChanges && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        session.HasUpstreamChanges.ShouldBeTrue();
    }

    private static ConfiglueRuntime<AppSettings, AppSettings.Fragment> CreateSubjectRuntime(
        SubjectStateStore<AppSettings.Fragment> users
    )
    {
        var builder = new StateSourceSetBuilder<AppSettings.Fragment>();
        builder.Add("user", users).KeyBy<SettingsSubject>(static subject => subject.Key);
        return new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            builder.Build(),
            onChangeDebounce: TimeSpan.Zero
        );
    }

    private static AppSettings.Fragment Fragment(
        string? label,
        int retryCount = 3,
        bool enabled = true
    ) =>
        new()
        {
            Label = Optional<string?>.Present(label),
            RetryCount = Optional<int>.Present(retryCount),
            Enabled = Optional<bool>.Present(enabled),
        };

    private sealed record SettingsSubject(string TenantId, string UserId) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.FromSegments(TenantId, UserId);
    }

    private sealed class MutableSubjectAccessor : IConfiglueSubjectAccessor
    {
        private IConfiglueSubject _subject;

        public MutableSubjectAccessor(IConfiglueSubject subject) => _subject = subject;

        public void Set(IConfiglueSubject subject) => _subject = subject;

        public ValueTask<IConfiglueSubject> GetCurrentSubjectAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(_subject);
        }
    }

    private sealed class UnsupportedState : IReadOnlyState<AppSettings>
    {
        public IDisposable OnChange(Action<AppSettings> listener) =>
            throw new NotSupportedException();

        public ValueTask<AppSettings> GetValueAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SignalingStore<T> : ISourceReader<T>, ISourceWriter<T>, ISourceWatcher
    {
        private readonly InMemoryStateSource<T> _inner;
        private readonly TaskCompletionSource _watchStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _readCount;

        public SignalingStore(T initialValue) => _inner = new InMemoryStateSource<T>(initialValue);

        public Task WatchStarted => _watchStarted.Task;

        public int ReadCount => Volatile.Read(ref _readCount);

        public void Set(T value) => _inner.Set(value);

        public async ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _readCount);
            return await _inner.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        ) => _inner.WriteAsync(context, request, cancellationToken);

        public async ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            _watchStarted.TrySetResult();
            await _inner
                .WaitForChangeAsync(context, observedRevision, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class SubjectStateStore<T>
        : ISourceReader<T>,
            ISourceWriter<T>,
            ISourceWatcher
    {
        private readonly ConcurrentDictionary<SubjectKey, InMemoryStateSource<T>> _states = new();

        public void Set(SubjectKey key, T value) => Get(key).Set(value);

        public StateReadResult<T> Read(SubjectKey key) =>
            Get(key).ReadAsync().GetAwaiter().GetResult();

        public ValueTask<StateReadResult<T>> ReadAsync(
            CancellationToken cancellationToken = default
        ) => Get(SubjectKey.Default).ReadAsync(cancellationToken);

        public ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => Get(context.Key).ReadAsync(cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        ) => Get(SubjectKey.Default).WriteAsync(request, cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        ) => Get(context.Key).WriteAsync(request, cancellationToken);

        public ValueTask WaitForChangeAsync(
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => Get(SubjectKey.Default).WaitForChangeAsync(observedRevision, cancellationToken);

        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => Get(context.Key).WaitForChangeAsync(observedRevision, cancellationToken);

        private InMemoryStateSource<T> Get(SubjectKey key) =>
            _states.GetOrAdd(key, static _ => new InMemoryStateSource<T>());
    }
}
