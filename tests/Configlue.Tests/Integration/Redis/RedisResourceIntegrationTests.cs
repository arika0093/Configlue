using Configlue.Resource.Redis;
using StackExchange.Redis;

namespace Configlue.Tests;

/// <summary>
/// Exercises the production Redis path (real StackExchange.Redis multiplexer, Lua execution,
/// hashes, and Pub/Sub) against a live Redis server.
/// </summary>
[RedisIntegration]
public sealed class RedisResourceIntegrationTests
{
    [Test]
    public async Task ReadWriteRoundTripsThroughRealBackend()
    {
        var ns = NewNamespace();
        using var resource = CreateResource(ns, ns);
        var context = CreateContext("subject-a");

        var created = await resource.WriteAsync(
            context,
            new ResourceWriteRequest(new byte[] { 1, 2, 3 }, RevisionCondition.MustNotExist)
        );
        created.Revision.ShouldBe("1");

        var read = await resource.ReadAsync(context);
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Content.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        read.Revision.ShouldBe("1");

        var updated = await resource.WriteAsync(
            context,
            new ResourceWriteRequest(new byte[] { 9 }, RevisionCondition.Match("1"))
        );
        updated.Revision.ShouldBe("2");
        (await resource.ReadAsync(context)).Content.ToArray().ShouldBe(new byte[] { 9 });
    }

    [Test]
    public async Task MustNotExistAllowsExactlyOneConcurrentWriter()
    {
        var ns = NewNamespace();
        using var resource = CreateResource(ns, ns);
        var context = CreateContext("subject");

        var writes = await Task.WhenAll(
            Enumerable
                .Range(0, 32)
                .Select(index =>
                    TryWriteAsync(
                        resource,
                        context,
                        (byte)index,
                        RevisionCondition.MustNotExist
                    )
                )
        );

        writes.Count(static succeeded => succeeded).ShouldBe(1);
        var read = await resource.ReadAsync(context);
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Revision.ShouldBe("1");

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                context,
                new ResourceWriteRequest(new byte[] { 99 }, RevisionCondition.MustNotExist)
            )
        );
    }

    [Test]
    public async Task MatchConditionAllowsExactlyOneConcurrentWriter()
    {
        var ns = NewNamespace();
        using var resource = CreateResource(ns, ns);
        var context = CreateContext("subject");
        await resource.WriteAsync(
            context,
            new ResourceWriteRequest(new byte[] { 1 }, RevisionCondition.MustNotExist)
        );

        var writes = await Task.WhenAll(
            Enumerable
                .Range(0, 32)
                .Select(index =>
                    TryWriteAsync(resource, context, (byte)index, RevisionCondition.Match("1"))
                )
        );

        writes.Count(static succeeded => succeeded).ShouldBe(1);
        (await resource.ReadAsync(context)).Revision.ShouldBe("2");

        var unconditional = await resource.WriteAsync(
            context,
            new ResourceWriteRequest(new byte[] { 100 })
        );
        unconditional.Revision.ShouldBe("3");
    }

    [Test]
    public async Task PersistsRevisionSchemaAndHashFields()
    {
        var ns = NewNamespace();
        using var resource = CreateResource(ns, ns);
        var context = CreateContext("subject");

        await resource.WriteAsync(
            context,
            new ResourceWriteRequest(
                new byte[] { 4, 5, 6 },
                RevisionCondition.MustNotExist,
                new StateSchemaMetadata("model-one", 3)
            )
        );

        var key = ComputeKey(ns, ns, modelId: null, "subject");
        var fields = ToMap(await Multiplexer.GetDatabase(0).HashGetAllAsync(key));
        fields.ShouldContainKey("updated_at");
        ((byte[]?)fields["payload"]!).ShouldBe(new byte[] { 4, 5, 6 });
        fields["revision"].ToString().ShouldBe("1");
        fields["schema_model_id"].ToString().ShouldBe("model-one");
        fields["schema_version"].ToString().ShouldBe("3");

        await resource.WriteAsync(
            context,
            new ResourceWriteRequest(new byte[] { 7 }, RevisionCondition.Match("1"))
        );

        var updated = ToMap(await Multiplexer.GetDatabase(0).HashGetAllAsync(key));
        updated["revision"].ToString().ShouldBe("2");
        updated.ShouldNotContainKey("schema_model_id");
        updated.ShouldNotContainKey("schema_version");
    }

    [Test]
    public async Task ModelSubjectAndRouteRowsAreIsolated()
    {
        var ns = NewNamespace();
        var routeA = RouteKey.From("primary-a");
        var routeB = RouteKey.From("primary-b");
        using var resource = new RedisResource(
            _ => Multiplexer,
            ns,
            new RedisResourceOptions
            {
                KeyPrefix = ns,
                DatabaseSelector = context => context.Route == routeA ? 1 : 2,
                NotificationChannel = ns + ":changed",
            }
        );

        var modelOne = CreateContext("tenant", routeA, modelId: "model-one");
        var modelTwo = CreateContext("tenant", routeA, modelId: "model-two");
        var otherSubject = CreateContext("other", routeA);
        var otherRoute = CreateContext("tenant", routeB);

        await resource.WriteAsync(
            modelOne,
            new ResourceWriteRequest(new byte[] { 1 }, RevisionCondition.MustNotExist)
        );
        await resource.WriteAsync(
            modelTwo,
            new ResourceWriteRequest(new byte[] { 2 }, RevisionCondition.MustNotExist)
        );
        await resource.WriteAsync(
            otherSubject,
            new ResourceWriteRequest(new byte[] { 3 }, RevisionCondition.MustNotExist)
        );
        await resource.WriteAsync(
            otherRoute,
            new ResourceWriteRequest(new byte[] { 4 }, RevisionCondition.MustNotExist)
        );

        (await resource.ReadAsync(modelOne)).Content.ToArray().ShouldBe(new byte[] { 1 });
        (await resource.ReadAsync(modelTwo)).Content.ToArray().ShouldBe(new byte[] { 2 });
        (await resource.ReadAsync(otherSubject)).Content.ToArray().ShouldBe(new byte[] { 3 });
        (await resource.ReadAsync(otherRoute)).Content.ToArray().ShouldBe(new byte[] { 4 });

        resource.GetResourceId(modelOne).ShouldNotBe(resource.GetResourceId(modelTwo));
        resource.GetResourceId(modelOne).ShouldNotBe(resource.GetResourceId(otherSubject));
        resource.GetResourceId(modelOne).ShouldNotBe(resource.GetResourceId(otherRoute));

        var modelOneKey = ComputeKey(ns, ns, "model-one", "tenant");
        var otherRouteKey = ComputeKey(ns, ns, modelId: null, "tenant");
        (await Multiplexer.GetDatabase(1).HashExistsAsync(modelOneKey, "revision"))
            .ShouldBeTrue();
        (await Multiplexer.GetDatabase(2).HashExistsAsync(otherRouteKey, "revision"))
            .ShouldBeTrue();
    }

    [Test]
    public async Task WaitersReceiveRealPubSubNotificationsAndAreScopedByIdentity()
    {
        var ns = NewNamespace();
        using var writer = CreateResource(ns, ns);
        using var watcher = CreateResource(ns, ns);
        var first = CreateContext("first");
        var second = CreateContext("second");
        await writer.WriteAsync(
            first,
            new ResourceWriteRequest(new byte[] { 1 }, RevisionCondition.MustNotExist)
        );

        var firstWait = watcher.WaitForChangeAsync(first, "1").AsTask();
        var secondWait = watcher.WaitForChangeAsync(second, null).AsTask();

        await Task.Delay(250);
        firstWait.IsCompleted.ShouldBeFalse();
        secondWait.IsCompleted.ShouldBeFalse();

        await writer.WriteAsync(
            first,
            new ResourceWriteRequest(new byte[] { 2 }, RevisionCondition.Match("1"))
        );
        await firstWait.WaitAsync(TimeSpan.FromSeconds(10));
        secondWait.IsCompleted.ShouldBeFalse();

        await writer.WriteAsync(second, new ResourceWriteRequest(new byte[] { 3 }));
        await secondWait.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task CancellationAndDisposalReleaseActiveWatchers()
    {
        var ns = NewNamespace();
        using (var resource = CreateResource(ns, ns))
        {
            var context = CreateContext("subject");
            await resource.WriteAsync(
                context,
                new ResourceWriteRequest(new byte[] { 1 }, RevisionCondition.MustNotExist)
            );

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await resource.WaitForChangeAsync(context, "1", cancellation.Token)
            );

            var wait = resource.WaitForChangeAsync(context, "1").AsTask();
            await Task.Delay(150);
            resource.Dispose();
            await wait.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static string NewNamespace() => "configlue-itest-" + Guid.NewGuid().ToString("N");

    private static RedisResource CreateResource(string ns, string keyPrefix) =>
        new(
            Multiplexer,
            ns,
            new RedisResourceOptions { KeyPrefix = keyPrefix, NotificationChannel = ns + ":changed" }
        );

    private static async Task<bool> TryWriteAsync(
        RedisResource resource,
        ConfiglueResourceContext context,
        byte value,
        RevisionCondition condition
    )
    {
        try
        {
            await resource.WriteAsync(context, new ResourceWriteRequest(new[] { value }, condition));
            return true;
        }
        catch (StateConflictException)
        {
            return false;
        }
    }

    private static string ComputeKey(
        string keyPrefix,
        string resourceNamespace,
        string? modelId,
        string subject
    ) =>
        $"{keyPrefix}:{RedisIdentityHash.Create(resourceNamespace, modelId ?? string.Empty, SubjectKey.From(subject).Value)}";

    private static Dictionary<string, RedisValue> ToMap(HashEntry[] entries) =>
        entries.ToDictionary(
            static entry => entry.Name.ToString(),
            static entry => entry.Value,
            StringComparer.Ordinal
        );

    private static ConfiglueResourceContext CreateContext(
        string subject,
        RouteKey route = default,
        string? modelId = null
    )
    {
        var key = SubjectKey.From(subject);
        return new ConfiglueResourceContext(modelId, new IntegrationSubject(key), key, route);
    }

    private static IConnectionMultiplexer Multiplexer => SharedMultiplexer.Value;

    private static readonly Lazy<IConnectionMultiplexer> SharedMultiplexer = new(() =>
        ConnectionMultiplexer.Connect(
            IntegrationEnvironment.RedisConnectionString + ",abortConnect=false,connectTimeout=5000"
        )
    );

    private sealed record IntegrationSubject(SubjectKey Key) : IConfiglueSubject;
}
