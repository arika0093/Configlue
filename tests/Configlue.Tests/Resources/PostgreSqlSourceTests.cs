using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Configlue.Provider.Json;
using Configlue.Source.PostgreSql;
using Npgsql;

namespace Configlue.Tests;

public sealed class PostgreSqlSourceTests
{
    [Test]
    public async Task CompareAndSwapAllowsOnlyOneConcurrentWriter()
    {
        var backend = new FakePostgreSqlStateBackend();
        using var source = CreateSource(_ => backend);
        var context = CreateContext("tenant-a");
        var created = await source.WriteAsync(
            context,
            new StateWriteRequest<string>("first", RevisionCondition.MustNotExist)
        );

        created.Revision.ShouldBe("1");
        var writes = await Task.WhenAll(
            Enumerable
                .Range(0, 24)
                .Select(index =>
                    TryWriteAsync(source, context, index.ToString(CultureInfo.InvariantCulture), created.Revision!)
                )
        );

        writes.Count(static succeeded => succeeded).ShouldBe(1);
        var current = await source.ReadAsync(context);
        current.Status.ShouldBe(StateReadStatus.Success);
        current.Revision.ShouldBe("2");

        await Should.ThrowAsync<StateConflictException>(async () =>
            await source.WriteAsync(
                context,
                new StateWriteRequest<string>("again", RevisionCondition.MustNotExist)
            )
        );
    }

    [Test]
    public async Task OneSourceServesManyKeysAndRoutesWithoutSharingRows()
    {
        var routeA = RouteKey.From("region-a");
        var routeB = RouteKey.From("region-b");
        var routeAStore = new FakePostgreSqlStateBackend();
        var routeBStore = new FakePostgreSqlStateBackend();
        var resolverCalls = new ConcurrentDictionary<RouteKey, int>();
        using var source = CreateSource(route =>
        {
            resolverCalls.AddOrUpdate(route, 1, static (_, count) => count + 1);
            return route == routeA ? routeAStore : routeBStore;
        });

        var contexts = Enumerable
            .Range(0, 40)
            .Select(index => CreateContext($"tenant-{index}", routeA))
            .ToArray();
        await Task.WhenAll(
            contexts.Select(
                (context, index) =>
                    source
                        .WriteAsync(
                            context,
                            new StateWriteRequest<string>(
                                index.ToString(CultureInfo.InvariantCulture),
                                RevisionCondition.MustNotExist
                            )
                        )
                        .AsTask()
            )
        );

        resolverCalls[routeA].ShouldBe(1);
        (await source.ReadAsync(contexts[17])).Value.ShouldBe("17");
        source.GetResourceId(contexts[0]).ShouldNotBe(source.GetResourceId(contexts[1]));

        var sameSubjectOnOtherRoute = CreateContext("tenant-0", routeB);
        await source.WriteAsync(
            sameSubjectOnOtherRoute,
            new StateWriteRequest<string>("200", RevisionCondition.MustNotExist)
        );
        resolverCalls[routeB].ShouldBe(1);
        (await source.ReadAsync(sameSubjectOnOtherRoute)).Value.ShouldBe("200");
        (await source.ReadAsync(CreateContext("tenant-0", routeA))).Value.ShouldBe("0");

        using var otherNamespace = CreateSource(_ => routeAStore, "other-application");
        await otherNamespace.WriteAsync(
            contexts[0],
            new StateWriteRequest<string>("201", RevisionCondition.MustNotExist)
        );
        (await otherNamespace.ReadAsync(contexts[0])).Value.ShouldBe("201");
        (await source.ReadAsync(contexts[0])).Value.ShouldBe("0");
    }

    [Test]
    public async Task RowsAreIsolatedByModelId()
    {
        var backend = new FakePostgreSqlStateBackend();
        using var source = CreateSource(_ => backend);
        var modelOne = CreateContext("tenant-a", modelId: "model-one");
        var modelTwo = CreateContext("tenant-a", modelId: "model-two");

        await source.WriteAsync(
            modelOne,
            new StateWriteRequest<string>("one", RevisionCondition.MustNotExist)
        );
        await source.WriteAsync(
            modelTwo,
            new StateWriteRequest<string>("two", RevisionCondition.MustNotExist)
        );

        source.GetResourceId(modelOne).ShouldNotBe(source.GetResourceId(modelTwo));
        (await source.ReadAsync(modelOne)).Value.ShouldBe("one");
        (await source.ReadAsync(modelTwo)).Value.ShouldBe("two");
    }

    [Test]
    public async Task WriteRejectsPayloadSchemaModelMismatch()
    {
        var backend = new FakePostgreSqlStateBackend();
        using var source = CreateFragmentSource(_ => backend);
        var context = CreateContext("tenant-a", modelId: "model-one");

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await source.WriteAsync(
                context,
                new StateWriteRequest<TestFragment>(new TestFragment("model-two", 1))
            )
        );
    }

    [Test]
    public async Task WriteAcceptsMatchingPayloadSchemaModel()
    {
        var backend = new FakePostgreSqlStateBackend();
        using var source = CreateFragmentSource(_ => backend);
        var context = CreateContext("tenant-a", modelId: "model-one");

        var write = await source.WriteAsync(
            context,
            new StateWriteRequest<TestFragment>(new TestFragment("model-one", 1))
        );

        write.Revision.ShouldBe("1");
    }

    [Test]
    public async Task WatcherInvalidationIsScopedToTheSubjectKey()
    {
        var backend = new FakePostgreSqlStateBackend();
        using var source = CreateSource(_ => backend);
        var firstContext = CreateContext("tenant-a");
        var secondContext = CreateContext("tenant-b");
        await source.WriteAsync(firstContext, new StateWriteRequest<string>("1"));
        await source.WriteAsync(secondContext, new StateWriteRequest<string>("2"));

        var firstWait = source.WaitForChangeAsync(firstContext, "1").AsTask();
        var secondWait = source.WaitForChangeAsync(secondContext, "1").AsTask();
        await backend.WaitForWaiterCountAsync(2).WaitAsync(TimeSpan.FromSeconds(2));

        await source.WriteAsync(
            firstContext,
            new StateWriteRequest<string>("3", RevisionCondition.Match("1"))
        );
        await firstWait.WaitAsync(TimeSpan.FromSeconds(2));
        secondWait.IsCompleted.ShouldBeFalse();

        await source.WriteAsync(
            secondContext,
            new StateWriteRequest<string>("4", RevisionCondition.Match("1"))
        );
        await secondWait.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task ChangeHubTargetsIdentityAndInvalidatesWaitersAfterReconnect()
    {
        var listener = new FakePostgreSqlNotificationListener();
        using var hub = new PostgreSqlChangeHub("channel", listener.RunAsync);
        var firstRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var secondRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstIdentity = PostgreSqlIdentityHash.Create("settings", "tenant-a");
        var secondIdentity = PostgreSqlIdentityHash.Create("settings", "tenant-b");

        var firstWait = hub.WaitForChangeAsync(
                firstIdentity,
                "1",
                _ =>
                {
                    firstRead.TrySetResult();
                    return ValueTask.FromResult(
                        ResourceReadResult.Success(new byte[] { 1 }, "1")
                    );
                },
                CancellationToken.None
            )
            .AsTask();
        var secondWait = hub.WaitForChangeAsync(
                secondIdentity,
                "1",
                _ =>
                {
                    secondRead.TrySetResult();
                    return ValueTask.FromResult(
                        ResourceReadResult.Success(new byte[] { 2 }, "1")
                    );
                },
                CancellationToken.None
            )
            .AsTask();
        await Task.WhenAll(listener.Started, firstRead.Task, secondRead.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));

        listener.Notify("channel", secondIdentity);
        await secondWait.WaitAsync(TimeSpan.FromSeconds(2));
        firstWait.IsCompleted.ShouldBeFalse();

        listener.Reconnect();
        await firstWait.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public void RejectsUnsafeTableIdentifiers()
    {
        Should.Throw<ArgumentException>(() =>
            CreateSource(
                _ => new FakePostgreSqlStateBackend(),
                tableOptions: new PostgreSqlTableOptions
                {
                    TableName = "settings; DROP TABLE users",
                }
            )
        );
    }

    [Test]
    public void ResourceIdentityTracksRoutesOnlyWhenRoutingIsConfigured()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=postgres;Username=postgres;Password=not-used"
        );
        var serializer = new JsonStateValueSerializer<string>();
        var defaultRouteContext = CreateContext("tenant-a");
        var routeContext = CreateContext("tenant-a", RouteKey.From("region-a"));
        using var fixedSource = new PostgreSqlSource<string>(dataSource, "settings", serializer);
        using var routedSource = new PostgreSqlSource<string>(
            _ => dataSource,
            "settings",
            serializer
        );

        fixedSource
            .GetResourceId(defaultRouteContext)
            .ShouldBe(fixedSource.GetResourceId(routeContext));
        routedSource
            .GetResourceId(defaultRouteContext)
            .ShouldNotBe(routedSource.GetResourceId(routeContext));
    }

    [Test]
    public void ChangeHubIsSharedByDataSourceAndListenerChannel()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=postgres;Username=postgres;Password=not-used"
        );
        using var first = PostgreSqlChangeHubRegistry.Acquire(dataSource, "clue_same_table");
        using var second = PostgreSqlChangeHubRegistry.Acquire(dataSource, "clue_same_table");
        using var otherTable = PostgreSqlChangeHubRegistry.Acquire(dataSource, "clue_other_table");

        ReferenceEquals(first.Hub, second.Hub).ShouldBeTrue();
        ReferenceEquals(first.Hub, otherTable.Hub).ShouldBeFalse();

        first.Dispose();
        second.Hub.IsDisposed.ShouldBeFalse();
        second.Dispose();
        second.Hub.IsDisposed.ShouldBeTrue();
        otherTable.Hub.IsDisposed.ShouldBeFalse();
        otherTable.Dispose();
    }

    private static PostgreSqlSource<string> CreateSource(
        Func<RouteKey, IPostgreSqlStateBackend> resolver,
        string resourceNamespace = "settings",
        PostgreSqlTableOptions? tableOptions = null
    ) => new(resolver, resourceNamespace, new JsonStateValueSerializer<string>(), tableOptions);

    private static PostgreSqlSource<TestFragment> CreateFragmentSource(
        Func<RouteKey, IPostgreSqlStateBackend> resolver,
        string resourceNamespace = "settings",
        PostgreSqlTableOptions? tableOptions = null
    ) =>
        new(
            resolver,
            resourceNamespace,
            JsonStateValueSerializer<TestFragment>.FromConverter(new TestFragmentConverter()),
            tableOptions
        );

    private static ConfiglueResourceContext CreateContext(
        string subject,
        RouteKey route = default,
        string? modelId = null
    )
    {
        var key = SubjectKey.From(subject);
        return new ConfiglueResourceContext(modelId, new FakeSubject(key), key, route);
    }

    private static async Task<bool> TryWriteAsync(
        PostgreSqlSource<string> source,
        ConfiglueResourceContext context,
        string value,
        string revision
    )
    {
        try
        {
            await source.WriteAsync(
                context,
                new StateWriteRequest<string>(value, RevisionCondition.Match(revision))
            );
            return true;
        }
        catch (StateConflictException)
        {
            return false;
        }
    }

    private sealed record FakeSubject(SubjectKey Key) : IConfiglueSubject;

    private sealed record TestFragment(string ModelId, int Version) : IConfiglueFragment
    {
        public ConfiglueModelSchema ConfiglueSchema =>
            new(typeof(TestFragment), ModelId, Version, []);

        public IEnumerable<SparseFragmentMember> EnumeratePresentMembers() => [];

        public ISparseFragment WithMember(int memberId, object? value) => this;

        public ISparseFragment WithoutMember(int memberId) => this;
    }

    private sealed class TestFragmentConverter : JsonConverter<TestFragment>
    {
        public override TestFragment? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) => throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            TestFragment value,
            JsonSerializerOptions options
        ) => writer.WriteStartObject();
    }

    private sealed class FakePostgreSqlStateBackend : IPostgreSqlStateBackend
    {
        private readonly object _gate = new();
        private readonly Dictionary<
            (string ModelId, string Namespace, string Key),
            StoredState
        > _states = [];
        private readonly Dictionary<
            (string ModelId, string Namespace, string Key),
            HashSet<TaskCompletionSource>
        > _waiters = [];
        private TaskCompletionSource _waitersChanged = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public ValueTask<ResourceReadResult> ReadAsync(
            string resourceNamespace,
            string modelId,
            string subjectKey,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult(
                    _states.TryGetValue((modelId, resourceNamespace, subjectKey), out var state)
                        ? ResourceReadResult.Success(
                            state.Content,
                            state.Revision.ToString(CultureInfo.InvariantCulture),
                            state.Schema
                        )
                        : ResourceReadResult.NotFound()
                );
            }
        }

        public ValueTask<StateWriteResult> WriteAsync(
            string resourceNamespace,
            string modelId,
            string subjectKey,
            ResourceWriteRequest request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource[] notifications;
            long revision;
            var address = (modelId, resourceNamespace, subjectKey);
            lock (_gate)
            {
                _states.TryGetValue(address, out var current);
                var currentRevision = current?.Revision.ToString(CultureInfo.InvariantCulture);
                if (!request.Condition.IsSatisfiedBy(currentRevision, current is not null))
                {
                    throw new StateConflictException("The in-memory PostgreSQL row changed.");
                }

                revision = (current?.Revision ?? 0) + 1;
                _states[address] = new StoredState(
                    request.Content.ToArray(),
                    revision,
                    request.Schema
                );
                notifications = _waiters.TryGetValue(address, out var waiting)
                    ? waiting.ToArray()
                    : [];
            }

            foreach (var notification in notifications)
            {
                notification.TrySetResult();
            }

            return ValueTask.FromResult(
                new StateWriteResult(revision.ToString(CultureInfo.InvariantCulture))
            );
        }

        public async ValueTask WaitForChangeAsync(
            string resourceNamespace,
            string modelId,
            string subjectKey,
            string? observedRevision,
            CancellationToken cancellationToken
        )
        {
            TaskCompletionSource signal;
            var address = (modelId, resourceNamespace, subjectKey);
            lock (_gate)
            {
                _states.TryGetValue(address, out var current);
                var currentRevision = current?.Revision.ToString(CultureInfo.InvariantCulture);
                if (!string.Equals(currentRevision, observedRevision, StringComparison.Ordinal))
                {
                    return;
                }

                signal = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                if (!_waiters.TryGetValue(address, out var waiting))
                {
                    waiting = [];
                    _waiters.Add(address, waiting);
                }

                waiting.Add(signal);
                _waitersChanged.TrySetResult();
                _waitersChanged = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
            }

            try
            {
                await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (_waiters.TryGetValue(address, out var waiting))
                    {
                        waiting.Remove(signal);
                        if (waiting.Count == 0)
                        {
                            _waiters.Remove(address);
                        }
                    }
                }
            }
        }

        public async Task WaitForWaiterCountAsync(int expectedCount)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_waiters.Values.Sum(static waiters => waiters.Count) >= expectedCount)
                    {
                        return;
                    }

                    changed = _waitersChanged.Task;
                }

                await changed.ConfigureAwait(false);
            }
        }

        public void Dispose() { }

        private sealed record StoredState(
            byte[] Content,
            long Revision,
            StateSchemaMetadata? Schema
        );
    }

    private sealed class FakePostgreSqlNotificationListener
    {
        private Action<string, string>? _onNotification;
        private Action? _onReconnect;
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task Started => _started.Task;

        public Task RunAsync(
            string channel,
            Action<string, string> onNotification,
            Action onReconnect,
            Action onReady,
            CancellationToken cancellationToken
        )
        {
            _onNotification = onNotification;
            _onReconnect = onReconnect;
            onReady();
            _started.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public void Notify(string channel, string payload) =>
            _onNotification!.Invoke(channel, payload);

        public void Reconnect() => _onReconnect!.Invoke();
    }
}
