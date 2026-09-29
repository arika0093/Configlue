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
        resource
            .GetResourceId(contexts[0])
            .ShouldNotBe(resource.GetResourceId(sameSubjectOnOtherRoute));
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
        await backend.WaitForWaiterCountAsync(2).WaitAsync(TimeSpan.FromSeconds(2));

        await resource.WriteAsync(
            first,
            new ResourceWriteRequest(new byte[] { 3 }, RevisionCondition.Match("1"))
        );
        await firstWait.WaitAsync(TimeSpan.FromSeconds(2));
        secondWait.IsCompleted.ShouldBeFalse();

        await resource.WriteAsync(
            second,
            new ResourceWriteRequest(new byte[] { 4 }, RevisionCondition.Match("1"))
        );
        await secondWait.WaitAsync(TimeSpan.FromSeconds(2));
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
                    return ValueTask.FromResult(ResourceReadResult.Success(new byte[] { 1 }, "1"));
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
                    return ValueTask.FromResult(ResourceReadResult.Success(new byte[] { 2 }, "1"));
                },
                CancellationToken.None
            )
            .AsTask();
        await Task.WhenAll(transport.Started, firstRead.Task, secondRead.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));

        transport.Channel.ShouldBe("watch-channel");
        transport.Notify("identity-b");
        await secondWait.WaitAsync(TimeSpan.FromSeconds(2));
        firstWait.IsCompleted.ShouldBeFalse();

        transport.Reconnect();
        await firstWait.WaitAsync(TimeSpan.FromSeconds(2));
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

    private static ConfiglueResourceContext CreateContext(string subject, RouteKey route = default)
    {
        var key = SubjectKey.From(subject);
        return new ConfiglueResourceContext(new FakeSubject(key), key, route);
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

        public ValueTask<ResourceReadResult> ReadAsync(
            RedisResourceAddress address,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult(
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

            return ValueTask.FromResult(
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
