using System.Collections.Concurrent;
using System.Globalization;
using Configlue.Resource.Redis;
using StackExchange.Redis;

namespace Configlue.Tests;

public sealed class RedisResourceTests
{
    [Test]
    public async Task LuaStyleConditionsAllowOnlyOneConcurrentWriter()
    {
        var backend = new FakeRedisStateBackend();
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

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                context,
                new ResourceWriteRequest(new byte[] { 99 }, RevisionCondition.MustNotExist)
            )
        );

        var unconditional = await resource.WriteAsync(
            context,
            new ResourceWriteRequest(new byte[] { 100 })
        );
        unconditional.Revision.ShouldBe("3");
    }

    [Test]
    public async Task OneResourceServesManySubjectKeysAndPhysicalRoutes()
    {
        var routeA = RouteKey.From("primary-a");
        var routeB = RouteKey.From("primary-b");
        var backendA = new FakeRedisStateBackend();
        var backendB = new FakeRedisStateBackend();
        var resolverCalls = new ConcurrentDictionary<RouteKey, int>();
        using var resource = CreateResource(
            route =>
            {
                resolverCalls.AddOrUpdate(route, 1, static (_, count) => count + 1);
                return route == routeA ? backendA : backendB;
            },
            options: new RedisResourceOptions
            {
                KeyPrefixSelector = context => $"settings:{context.Route.Value}",
                DatabaseSelector = context => context.Route == routeA ? 1 : 2,
            }
        );

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
        resource.CachedBackendCount.ShouldBe(1);
        (await resource.ReadAsync(contexts[17])).Content.ToArray().ShouldBe(new byte[] { 17 });
        resource.GetResourceId(contexts[0]).ShouldNotBe(resource.GetResourceId(contexts[1]));

        var sameSubjectOnOtherRoute = CreateContext("tenant-0", routeB);
        await resource.WriteAsync(
            sameSubjectOnOtherRoute,
            new ResourceWriteRequest(new byte[] { 200 }, RevisionCondition.MustNotExist)
        );
        resolverCalls[routeB].ShouldBe(1);
        resource.CachedBackendCount.ShouldBe(2);
        (await resource.ReadAsync(sameSubjectOnOtherRoute))
            .Content.ToArray()
            .ShouldBe(new byte[] { 200 });
        resource
            .GetResourceId(contexts[0])
            .ShouldNotBe(resource.GetResourceId(sameSubjectOnOtherRoute));
    }

    [Test]
    public async Task RowsAreIsolatedByModelId()
    {
        var backend = new FakeRedisStateBackend();
        using var resource = CreateResource(_ => backend);
        var modelOne = CreateContext("tenant-a", modelId: "model-one");
        var modelTwo = CreateContext("tenant-a", modelId: "model-two");

        await resource.WriteAsync(
            modelOne,
            new ResourceWriteRequest(new byte[] { 1 }, RevisionCondition.MustNotExist)
        );
        await resource.WriteAsync(
            modelTwo,
            new ResourceWriteRequest(new byte[] { 2 }, RevisionCondition.MustNotExist)
        );

        resource.GetResourceId(modelOne).ShouldNotBe(resource.GetResourceId(modelTwo));
        (await resource.ReadAsync(modelOne)).Content.ToArray().ShouldBe(new byte[] { 1 });
        (await resource.ReadAsync(modelTwo)).Content.ToArray().ShouldBe(new byte[] { 2 });

        var addresses = backend.Addresses.ToArray();
        addresses.Select(static address => address.Key).Distinct().Count().ShouldBe(2);
        addresses
            .Select(static address => address.NotificationIdentity)
            .Distinct()
            .Count()
            .ShouldBe(2);
    }

    [Test]
    public void SameModelNamespaceAndKeyProduceStableIdentity()
    {
        var backend = new FakeRedisStateBackend();
        using var resource = CreateResource(_ => backend);
        var first = CreateContext("tenant-a", modelId: "model-one");
        var second = CreateContext("tenant-a", modelId: "model-one");

        resource.GetResourceId(first).ShouldBe(resource.GetResourceId(second));
    }

    [Test]
    public async Task NullModelIdRemainsSupported()
    {
        var backend = new FakeRedisStateBackend();
        using var resource = CreateResource(_ => backend);
        var context = CreateContext("tenant-a");

        await resource.WriteAsync(
            context,
            new ResourceWriteRequest(new byte[] { 7 }, RevisionCondition.MustNotExist)
        );

        (await resource.ReadAsync(context)).Content.ToArray().ShouldBe(new byte[] { 7 });
        resource.GetResourceId(context).ShouldBe(resource.GetResourceId(CreateContext("tenant-a")));
    }

    [Test]
    public async Task WaitersAreInvalidatedOnlyForTheirModel()
    {
        var backend = new FakeRedisStateBackend();
        using var resource = CreateResource(_ => backend);
        var modelOne = CreateContext("tenant-a", modelId: "model-one");
        var modelTwo = CreateContext("tenant-a", modelId: "model-two");
        await resource.WriteAsync(modelOne, new ResourceWriteRequest(new byte[] { 1 }));
        await resource.WriteAsync(modelTwo, new ResourceWriteRequest(new byte[] { 2 }));

        var wait = resource.WaitForChangeAsync(modelOne, "1").AsTask();
        await backend.WaitForWaiterCountAsync(1).WaitAsync(TimeSpan.FromSeconds(30));

        await resource.WriteAsync(
            modelTwo,
            new ResourceWriteRequest(new byte[] { 3 }, RevisionCondition.Match("1"))
        );
        wait.IsCompleted.ShouldBeFalse();

        await resource.WriteAsync(
            modelOne,
            new ResourceWriteRequest(new byte[] { 4 }, RevisionCondition.Match("1"))
        );
        await wait.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task WaitersAreInvalidatedOnlyForTheirRedisRow()
    {
        var backend = new FakeRedisStateBackend();
        using var resource = CreateResource(_ => backend);
        var first = CreateContext("tenant-a");
        var second = CreateContext("tenant-b");
        await resource.WriteAsync(first, new ResourceWriteRequest(new byte[] { 1 }));
        await resource.WriteAsync(second, new ResourceWriteRequest(new byte[] { 2 }));

        var firstWait = resource.WaitForChangeAsync(first, "1").AsTask();
        var secondWait = resource.WaitForChangeAsync(second, "1").AsTask();
        await backend.WaitForWaiterCountAsync(2).WaitAsync(TimeSpan.FromSeconds(30));

        await resource.WriteAsync(
            first,
            new ResourceWriteRequest(new byte[] { 3 }, RevisionCondition.Match("1"))
        );
        await firstWait.WaitAsync(TimeSpan.FromSeconds(30));
        secondWait.IsCompleted.ShouldBeFalse();

        await resource.WriteAsync(
            second,
            new ResourceWriteRequest(new byte[] { 4 }, RevisionCondition.Match("1"))
        );
        await secondWait.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task ChangeHubFansOutByIdentityAndInvalidatesAfterReconnect()
    {
        var transport = new FakeRedisNotificationTransport();
        using var hub = new RedisChangeHub(transport, "watch-channel");
        var firstRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var secondRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var firstWait = hub.WaitForChangeAsync(
                "identity-a",
                "1",
                _ =>
                {
                    firstRead.TrySetResult();
                    return ValueTaskCompat.FromResult(
                        ResourceReadResult.Success(new byte[] { 1 }, "1")
                    );
                },
                CancellationToken.None
            )
            .AsTask();
        var secondWait = hub.WaitForChangeAsync(
                "identity-b",
                "1",
                _ =>
                {
                    secondRead.TrySetResult();
                    return ValueTaskCompat.FromResult(
                        ResourceReadResult.Success(new byte[] { 2 }, "1")
                    );
                },
                CancellationToken.None
            )
            .AsTask();
        await Task.WhenAll(transport.Started, firstRead.Task, secondRead.Task)
            .WaitAsync(TimeSpan.FromSeconds(30));

        transport.Channel.ShouldBe("watch-channel");
        transport.Notify("identity-b");
        await secondWait.WaitAsync(TimeSpan.FromSeconds(30));
        firstWait.IsCompleted.ShouldBeFalse();

        transport.Reconnect();
        await firstWait.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    [NotInParallel]
    public async Task ChangeHubLastWaiterCleanupDoesNotDetachNewWaiter_Notification()
    {
        var transport = new FakeRedisNotificationTransport();
        using var hub = new RedisChangeHub(transport, "watch-channel");
        const string identity = "identity-race-notification";

        var secondWait = await RaceLastWaiterCleanupAsync(hub, transport, identity);
        secondWait.IsCompleted.ShouldBeFalse();

        transport.Notify(identity);
        await secondWait.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForRedisEntryCountAsync(hub, 0);
    }

    [Test]
    [NotInParallel]
    public async Task ChangeHubLastWaiterCleanupDoesNotDetachNewWaiter_Reconnect()
    {
        var transport = new FakeRedisNotificationTransport();
        using var hub = new RedisChangeHub(transport, "watch-channel");
        const string identity = "identity-race-reconnect";

        var secondWait = await RaceLastWaiterCleanupAsync(hub, transport, identity);
        secondWait.IsCompleted.ShouldBeFalse();

        transport.Reconnect();
        await secondWait.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForRedisEntryCountAsync(hub, 0);
    }

    [Test]
    [NotInParallel]
    public async Task ChangeHubLastWaiterCleanupDoesNotDetachNewWaiter_Dispose()
    {
        var transport = new FakeRedisNotificationTransport();
        var hub = new RedisChangeHub(transport, "watch-channel");
        const string identity = "identity-race-dispose";

        try
        {
            var secondWait = await RaceLastWaiterCleanupAsync(hub, transport, identity);
            secondWait.IsCompleted.ShouldBeFalse();

            hub.Dispose();
            await secondWait.WaitAsync(TimeSpan.FromSeconds(30));
            hub.TestWaiterEntryCount.ShouldBe(0);
        }
        finally
        {
            hub.Dispose();
        }
    }

    private static async Task<Task> RaceLastWaiterCleanupAsync(
        RedisChangeHub hub,
        FakeRedisNotificationTransport transport,
        string identity
    )
    {
        static ValueTask<ResourceReadResult> ReadSameRevision(CancellationToken _) =>
            ValueTaskCompat.FromResult(ResourceReadResult.Success(new byte[] { 1 }, "1"));

        // Coordination uses TaskCompletionSources so the test thread awaits
        // asynchronously instead of blocking a thread-pool thread. The cleanup hook
        // still blocks synchronously while holding the hub's waiter gate -- that is
        // intentional: it pins the last-waiter cleanup inside its critical section
        // while the second waiter attempts to register. Generous 30s budgets keep
        // this deterministic on loaded Windows CI where thread-pool injection is
        // throttled.
        var stableTimeout = TimeSpan.FromSeconds(30);
        var cleanupReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var registerAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var secondRegistered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cleanupCalls = 0;
        hub.TestHookOnCleanupRemoving = () =>
        {
            if (Interlocked.Increment(ref cleanupCalls) == 1)
            {
                cleanupReached.TrySetResult();
                if (!releaseCleanup.Task.Wait(stableTimeout))
                {
                    throw new TimeoutException("Timed out waiting to release Redis cleanup.");
                }
            }
        };

        var firstWait = hub.WaitForChangeAsync(
                identity,
                "1",
                static token => ReadSameRevision(token),
                CancellationToken.None
            )
            .AsTask();
        await transport.Started.WaitAsync(stableTimeout);
        await WaitForRedisEntryCountAsync(hub, 1);

        transport.Notify(identity);
        await cleanupReached.Task.WaitAsync(stableTimeout).ConfigureAwait(false);

        hub.TestHookOnRegisterAttempt = () => registerAttempted.TrySetResult();
        hub.TestHookOnRegistered = () => secondRegistered.TrySetResult();
        // LongRunning gets a dedicated thread so second-waiter scheduling does not
        // depend on thread-pool injection while the cleanup thread is blocked.
        var secondWait = Task.Factory.StartNew(
            () =>
                hub.WaitForChangeAsync(
                    identity,
                    "1",
                    static token => ReadSameRevision(token),
                    CancellationToken.None
                )
                .AsTask(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        ).Unwrap();
        await registerAttempted.Task.WaitAsync(stableTimeout).ConfigureAwait(false);

        // Give the second waiter a chance to block on the waiter gate (it fires
        // OnRegisterAttempt before acquiring the gate). The exact delay is
        // best-effort only: correctness holds whether the second waiter blocks or
        // registers after cleanup, but blocking exercises the intended race.
        // Poll briefly for the blocked state instead of assuming a fixed 100ms is
        // enough on slow CI.
        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        releaseCleanup.TrySetResult();
        await firstWait.WaitAsync(stableTimeout).ConfigureAwait(false);
        await secondRegistered.Task.WaitAsync(stableTimeout).ConfigureAwait(false);

        hub.TestHookOnCleanupRemoving = null;
        hub.TestHookOnRegisterAttempt = null;
        hub.TestHookOnRegistered = null;
        await WaitForRedisEntryCountAsync(hub, 1);
        return secondWait;
    }

    private static async Task WaitForRedisEntryCountAsync(RedisChangeHub hub, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (hub.TestWaiterEntryCount != expected)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                hub.TestWaiterEntryCount.ShouldBe(expected);
            }

            await Task.Delay(10);
        }
    }

    [Test]
    public void FixedMultiplexerIdentityIgnoresRoutesAndRoutedIdentityIncludesThem()
    {
        using var multiplexer = ConnectionMultiplexer.Connect(
            "localhost:6399,abortConnect=false,connectTimeout=100,connectRetry=0"
        );
        var defaultRoute = CreateContext("tenant-a");
        var routed = CreateContext("tenant-a", RouteKey.From("primary-a"));
        using var fixedResource = new RedisResource(multiplexer, "settings");
        using var routedResource = new RedisResource(_ => multiplexer, "settings");

        fixedResource.GetResourceId(defaultRoute).ShouldBe(fixedResource.GetResourceId(routed));
        routedResource
            .GetResourceId(defaultRoute)
            .ShouldNotBe(routedResource.GetResourceId(routed));
    }

    [Test]
    public void RejectsInvalidDatabaseNumbers()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new RedisResourceOptions { Database = -2 }.Validate()
        );
    }

    private static RedisResource CreateResource(
        Func<RouteKey, IRedisStateBackend> resolver,
        string resourceNamespace = "settings",
        RedisResourceOptions? options = null
    ) => new(resolver, resourceNamespace, options);

    private static ConfiglueResourceContext CreateContext(
        string subject,
        RouteKey route = default,
        string? modelId = null
    )
    {
        var key = SubjectKey.From(subject);
        return new ConfiglueResourceContext(modelId, new FakeSubject(key), ResourceKey.From(key), route);
    }

    private static async Task<bool> TryWriteAsync(
        RedisResource resource,
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

    private sealed class FakeRedisStateBackend : IRedisStateBackend
    {
        private readonly object _gate = new();
        private readonly Dictionary<(int Database, string Key), StoredState> _states = [];
        private readonly Dictionary<
            (int Database, string Key),
            HashSet<TaskCompletionSource>
        > _waiters = [];
        private TaskCompletionSource _waitersChanged = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public ConcurrentQueue<RedisResourceAddress> Addresses { get; } = new();

        public ValueTask<ResourceReadResult> ReadAsync(
            RedisResourceAddress address,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Addresses.Enqueue(address);
            lock (_gate)
            {
                return ValueTaskCompat.FromResult(
                    _states.TryGetValue((address.Database, address.Key), out var state)
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
            RedisResourceAddress address,
            ResourceWriteRequest request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Addresses.Enqueue(address);
            TaskCompletionSource[] notifications;
            long revision;
            var key = (address.Database, address.Key);
            lock (_gate)
            {
                _states.TryGetValue(key, out var current);
                var currentRevision = current?.Revision.ToString(CultureInfo.InvariantCulture);
                if (!request.Condition.IsSatisfiedBy(currentRevision, current is not null))
                {
                    throw new StateConflictException("The Redis row changed.");
                }

                revision = (current?.Revision ?? 0) + 1;
                _states[key] = new StoredState(request.Content.ToArray(), revision, request.Schema);
                notifications = _waiters.TryGetValue(key, out var waiting) ? waiting.ToArray() : [];
            }

            foreach (var notification in notifications)
            {
                notification.TrySetResult();
            }

            return ValueTaskCompat.FromResult(
                new StateWriteResult(revision.ToString(CultureInfo.InvariantCulture))
            );
        }

        public async ValueTask WaitForChangeAsync(
            RedisResourceAddress address,
            string? observedRevision,
            CancellationToken cancellationToken
        )
        {
            TaskCompletionSource signal;
            var key = (address.Database, address.Key);
            lock (_gate)
            {
                _states.TryGetValue(key, out var current);
                var revision = current?.Revision.ToString(CultureInfo.InvariantCulture);
                if (!string.Equals(revision, observedRevision, StringComparison.Ordinal))
                {
                    return;
                }

                signal = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                if (!_waiters.TryGetValue(key, out var waiting))
                {
                    waiting = [];
                    _waiters.Add(key, waiting);
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
                    if (_waiters.TryGetValue(key, out var waiting))
                    {
                        waiting.Remove(signal);
                        if (waiting.Count == 0)
                        {
                            _waiters.Remove(key);
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

    private sealed class FakeRedisNotificationTransport : IRedisNotificationTransport
    {
        private Action<string>? _onNotification;
        private Action? _onReconnect;
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public string? Channel { get; private set; }

        public Task Started => _started.Task;

        public Task SubscribeAsync(
            string channel,
            Action<string> onNotification,
            Action onReconnect
        )
        {
            Channel = channel;
            _onNotification = onNotification;
            _onReconnect = onReconnect;
            _started.TrySetResult();
            return Task.CompletedTask;
        }

        public void Notify(string payload) => _onNotification!.Invoke(payload);

        public void Reconnect() => _onReconnect!.Invoke();

        public void Dispose() { }
    }
}
