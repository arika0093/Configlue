using System.Collections.Concurrent;
using System.Globalization;
using Configlue.Resource.PostgreSql;
using Npgsql;

namespace Configlue.Tests;

public sealed class PostgreSqlResourceTests
{
    [Test]
    public async Task CompareAndSwapAllowsOnlyOneConcurrentWriter()
    {
        var backend = new FakePostgreSqlStateBackend();
        using var resource = CreateResource(_ => backend);
        var context = CreateContext("tenant-a");
        var created = await resource.WriteAsync(
            context,
            new ResourceWriteRequest(new byte[] { 1 }, RevisionCondition.MustNotExist)
        );

        created.Revision.ShouldBe("1");
        var writes = await Task.WhenAll(
            Enumerable
                .Range(0, 24)
                .Select(index => TryWriteAsync(resource, context, (byte)index, created.Revision!))
        );

        writes.Count(static succeeded => succeeded).ShouldBe(1);
        var current = await resource.ReadAsync(context);
        current.Status.ShouldBe(StateReadStatus.Success);
        current.Revision.ShouldBe("2");
        current.Content.Length.ShouldBe(1);

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                context,
                new ResourceWriteRequest(new byte[] { 99 }, RevisionCondition.MustNotExist)
            )
        );
    }

    [Test]
    public async Task OneResourceServesManyKeysAndRoutesWithoutSharingRows()
    {
        var routeA = RouteKey.From("region-a");
        var routeB = RouteKey.From("region-b");
        var routeAStore = new FakePostgreSqlStateBackend();
        var routeBStore = new FakePostgreSqlStateBackend();
        var resolverCalls = new ConcurrentDictionary<RouteKey, int>();
        using var resource = CreateResource(route =>
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
                    resource
                        .WriteAsync(
                            context,
                            new ResourceWriteRequest(
                                new byte[] { (byte)index },
                                RevisionCondition.MustNotExist
                            )
                        )
                        .AsTask()
            )
        );

        resolverCalls[routeA].ShouldBe(1);
        (await resource.ReadAsync(contexts[17])).Content.ToArray().ShouldBe(new byte[] { 17 });
        resource.GetResourceId(contexts[0]).ShouldNotBe(resource.GetResourceId(contexts[1]));

        var sameSubjectOnOtherRoute = CreateContext("tenant-0", routeB);
        await resource.WriteAsync(
            sameSubjectOnOtherRoute,
            new ResourceWriteRequest(new byte[] { 200 }, RevisionCondition.MustNotExist)
        );
        resolverCalls[routeB].ShouldBe(1);
        (await resource.ReadAsync(sameSubjectOnOtherRoute))
            .Content.ToArray()
            .ShouldBe(new byte[] { 200 });
        (await resource.ReadAsync(CreateContext("tenant-0", routeA)))
            .Content.ToArray()
            .ShouldBe(new byte[] { 0 });

        using var otherNamespace = CreateResource(_ => routeAStore, "other-application");
        await otherNamespace.WriteAsync(
            contexts[0],
            new ResourceWriteRequest(new byte[] { 201 }, RevisionCondition.MustNotExist)
        );
        (await otherNamespace.ReadAsync(contexts[0]))
            .Content.ToArray()
            .ShouldBe(new byte[] { 201 });
        (await resource.ReadAsync(contexts[0])).Content.ToArray().ShouldBe(new byte[] { 0 });
    }

    [Test]
    public async Task WatcherInvalidationIsScopedToTheSubjectKey()
    {
        var backend = new FakePostgreSqlStateBackend();
        using var resource = CreateResource(_ => backend);
        var firstContext = CreateContext("tenant-a");
        var secondContext = CreateContext("tenant-b");
        await resource.WriteAsync(firstContext, new ResourceWriteRequest(new byte[] { 1 }));
        await resource.WriteAsync(secondContext, new ResourceWriteRequest(new byte[] { 2 }));

        var firstWait = resource.WaitForChangeAsync(firstContext, "1").AsTask();
        var secondWait = resource.WaitForChangeAsync(secondContext, "1").AsTask();
        await backend.WaitForWaiterCountAsync(2).WaitAsync(TimeSpan.FromSeconds(2));

        await resource.WriteAsync(
            firstContext,
            new ResourceWriteRequest(new byte[] { 3 }, RevisionCondition.Match("1"))
        );
        await firstWait.WaitAsync(TimeSpan.FromSeconds(2));
        secondWait.IsCompleted.ShouldBeFalse();

        await resource.WriteAsync(
            secondContext,
            new ResourceWriteRequest(new byte[] { 4 }, RevisionCondition.Match("1"))
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
                    return ValueTask.FromResult(ResourceReadResult.Success(new byte[] { 1 }, "1"));
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
                    return ValueTask.FromResult(ResourceReadResult.Success(new byte[] { 2 }, "1"));
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
            CreateResource(
                _ => new FakePostgreSqlStateBackend(),
                options: new PostgreSqlResourceOptions { TableName = "settings; DROP TABLE users" }
            )
        );
    }

    [Test]
    public void ResourceIdentityTracksRoutesOnlyWhenRoutingIsConfigured()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=postgres;Username=postgres;Password=not-used"
        );
        var defaultRouteContext = CreateContext("tenant-a");
        var routeContext = CreateContext("tenant-a", RouteKey.From("region-a"));
        using var fixedResource = new PostgreSqlResource(dataSource, "settings");
        using var routedResource = new PostgreSqlResource(_ => dataSource, "settings");

        fixedResource
            .GetResourceId(defaultRouteContext)
            .ShouldBe(fixedResource.GetResourceId(routeContext));
        routedResource
            .GetResourceId(defaultRouteContext)
            .ShouldNotBe(routedResource.GetResourceId(routeContext));
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

    private static PostgreSqlResource CreateResource(
        Func<RouteKey, IPostgreSqlStateBackend> resolver,
        string resourceNamespace = "settings",
        PostgreSqlResourceOptions? options = null
    ) => new(resolver, resourceNamespace, options);

    private static ConfiglueResourceContext CreateContext(string subject, RouteKey route = default)
    {
        var key = SubjectKey.From(subject);
        return new ConfiglueResourceContext(new FakeSubject(key), key, route);
    }

    private static async Task<bool> TryWriteAsync(
        PostgreSqlResource resource,
        ConfiglueResourceContext context,
        byte value,
        string revision
    )
    {
        try
        {
            await resource.WriteAsync(
                context,
                new ResourceWriteRequest(new byte[] { value }, RevisionCondition.Match(revision))
            );
            return true;
        }
        catch (StateConflictException)
        {
            return false;
        }
    }

    private sealed record FakeSubject(SubjectKey Key) : IConfiglueSubject;

    private sealed class FakePostgreSqlStateBackend : IPostgreSqlStateBackend
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Namespace, string Key), StoredState> _states = [];
        private readonly Dictionary<
            (string Namespace, string Key),
            HashSet<TaskCompletionSource>
        > _waiters = [];
        private TaskCompletionSource _waitersChanged = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public ValueTask<ResourceReadResult> ReadAsync(
            string resourceNamespace,
            string subjectKey,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult(
                    _states.TryGetValue((resourceNamespace, subjectKey), out var state)
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
            string subjectKey,
            ResourceWriteRequest request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource[] notifications;
            long revision;
            var address = (resourceNamespace, subjectKey);
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
            string subjectKey,
            string? observedRevision,
            CancellationToken cancellationToken
        )
        {
            TaskCompletionSource signal;
            var address = (resourceNamespace, subjectKey);
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
