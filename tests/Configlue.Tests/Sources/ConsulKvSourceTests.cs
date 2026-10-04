using System.Globalization;
using System.Net;
using System.Text;
using Configlue.Codecs;
using Configlue.Provider.Json;
using Configlue.Source.Consul;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class ConsulKvSourceTests
{
    [Test]
    public async Task ValueTaskCompatRoundTripsForNet48Compatibility()
    {
        var value = await ValueTaskCompat.FromResult(42);
        value.ShouldBe(42);
    }

    [Test]
    public async Task ReadsRecursivePrefixWithNestedMapping()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled", "false");
        client.Seed("config/app/retrycount", "7");
        client.Seed("config/app/label", "consul-label");
        client.Seed("config/app/database/host", "consul-db.example.test");
        client.Seed("config/app/database/port", "5433");
        using var source = CreatePrefixSource(client, "config/app");

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Revision.ShouldNotBeNullOrWhiteSpace();
        var fragment = read.Value!;
        fragment.Enabled.IsPresent.ShouldBeTrue();
        fragment.Enabled.Value.ShouldBeFalse();
        fragment.RetryCount.Value.ShouldBe(7);
        fragment.Label.Value.ShouldBe("consul-label");
        fragment.Database.IsPresent.ShouldBeTrue();
        fragment.Database.Value!.Host.Value.ShouldBe("consul-db.example.test");
        fragment.Database.Value.Port.Value.ShouldBe(5433);
        read.Schema.ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
    }

    [Test]
    public async Task MissingPrefixReturnsNotFoundWithRevision()
    {
        var client = new FakeConsulKvClient();
        using var source = CreatePrefixSource(client, "config/app");

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.NotFound);
        read.Revision.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task DeletedKeysReadAsAbsentMembers()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled", "true");
        client.Seed("config/app/database/host", "db.example.test");
        client.Seed("config/app/database/port", "5433");
        using var source = CreatePrefixSource(client, "config/app");

        var before = await source.ReadAsync();
        before.Value!.Database.Value!.Host.IsPresent.ShouldBeTrue();

        client.DeleteKey("config/app/database/host");
        var after = await source.ReadAsync();

        after.Status.ShouldBe(StateReadStatus.Success);
        after.Value!.Enabled.Value.ShouldBeTrue();
        after.Value.Database.IsPresent.ShouldBeTrue();
        after.Value.Database.Value!.Host.IsPresent.ShouldBeFalse();
        after.Value.Database.Value.Port.Value.ShouldBe(5433);
    }

    [Test]
    public async Task DuplicateMappedKeysFailTheRead()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled", "true");
        client.Seed("config/app/ENABLED", "false");
        using var source = CreatePrefixSource(client, "config/app");

        await Should.ThrowAsync<InvalidOperationException>(async () => await source.ReadAsync());
    }

    [Test]
    public async Task KeyContinuingPastScalarIsMalformed()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled/extra", "true");
        using var source = CreatePrefixSource(client, "config/app");

        await Should.ThrowAsync<FormatException>(async () => await source.ReadAsync());
    }

    [Test]
    public async Task MalformedValuesReturnInvalidPayload()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/retrycount", "not-an-int");
        using var source = CreatePrefixSource(client, "config/app");

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.InvalidPayload);
        read.Revision.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ReadForwardsConsistencyDatacenterAndNamespace()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled", "true");
        using var source = new ConsulKvSource<AppSettings.Fragment>(
            client,
            AppSettings.ConfiglueSchema,
            "config/app",
            new ConsulKvPrefixOptions
            {
                Datacenter = "dc1",
                Namespace = "team-a",
                Partition = "part-1",
                Consistency = ConsulConsistencyMode.Consistent,
            }
        );

        await source.ReadAsync();

        client.LastListOptions!.Datacenter.ShouldBe("dc1");
        client.LastListOptions.Namespace.ShouldBe("team-a");
        client.LastListOptions.Partition.ShouldBe("part-1");
        client.LastListOptions.Consistency.ShouldBe(ConsulConsistencyMode.Consistent);
    }

    [Test]
    public async Task WatchBlockingQuerySignalsChange()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled", "true");
        using var source = CreatePrefixSource(
            client,
            "config/app",
            blockingWait: TimeSpan.FromSeconds(5)
        );
        var first = await source.ReadAsync();

        var watch = source.WaitForChangeAsync(first.Revision).AsTask();
        await Task.Delay(50);
        watch.IsCompleted.ShouldBeFalse();

        client.Seed("config/app/retrycount", "9");
        await watch;

        var second = await source.ReadAsync();
        second.Value!.RetryCount.Value.ShouldBe(9);
        second.Revision.ShouldNotBe(first.Revision);
    }

    [Test]
    public async Task WatchToleratesSpuriousWakeupWithSameIndex()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled", "true");
        using var source = CreatePrefixSource(
            client,
            "config/app",
            blockingWait: TimeSpan.FromMilliseconds(100)
        );
        var first = await source.ReadAsync();

        // No change: the blocking query times out and the watcher returns level-triggered.
        await source.WaitForChangeAsync(first.Revision);

        var second = await source.ReadAsync();
        second.Revision.ShouldBe(first.Revision);
    }

    [Test]
    public async Task WatchRetriesAfterReconnectableFailure()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled", "true");
        using var source = CreatePrefixSource(
            client,
            "config/app",
            blockingWait: TimeSpan.FromSeconds(5)
        );
        var first = await source.ReadAsync();
        client.FailNextList(1);

        var watch = source.WaitForChangeAsync(first.Revision).AsTask();
        await Task.Delay(700);
        client.Seed("config/app/label", "reconnected");
        await watch;
        client.ListCalls.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task WatchTreatsIndexResetAsChange()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled", "true");
        using var source = CreatePrefixSource(
            client,
            "config/app",
            blockingWait: TimeSpan.FromSeconds(5)
        );
        var first = await source.ReadAsync();

        var watch = source.WaitForChangeAsync(first.Revision).AsTask();
        await Task.Delay(50);
        client.ResetIndex(1);
        await watch;
    }

    [Test]
    public async Task WatchHonorsCancellation()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled", "true");
        using var source = CreatePrefixSource(
            client,
            "config/app",
            blockingWait: TimeSpan.FromSeconds(30)
        );
        var first = await source.ReadAsync();
        using var cancellable = new CancellationTokenSource();

        var watch = source.WaitForChangeAsync(first.Revision, cancellable.Token).AsTask();
        await Task.Delay(50);
        await cancellable.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await watch);
    }

    [Test]
    public async Task DisposedSourceWakesWatcher()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled", "true");
        var source = CreatePrefixSource(
            client,
            "config/app",
            blockingWait: TimeSpan.FromSeconds(30)
        );
        var first = await source.ReadAsync();

        var watch = source.WaitForChangeAsync(first.Revision).AsTask();
        await Task.Delay(50);
        source.Dispose();
        await watch;
    }

    [Test]
    public async Task StaleCasSurfacesAsConflict()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled", "true");
        using var source = CreatePrefixSource(client, "config/app");
        var first = await source.ReadAsync();

        client.Seed("config/app/label", "external");
        var stale = new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) };

        await Should.ThrowAsync<StateConflictException>(async () =>
            await source.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(
                    stale,
                    RevisionCondition.Match(first.Revision!)
                )
            )
        );
    }

    [Test]
    public async Task MustNotExistConflictsWhenPrefixExists()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/enabled", "true");
        using var source = CreatePrefixSource(client, "config/app");

        await Should.ThrowAsync<StateConflictException>(async () =>
            await source.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) },
                    RevisionCondition.MustNotExist
                )
            )
        );
    }

    [Test]
    public async Task MultiKeyWriteUsesOneAtomicTransaction()
    {
        var client = new FakeConsulKvClient();
        using var source = CreatePrefixSource(client, "config/app");
        var fragment = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            RetryCount = Optional<int>.Present(11),
            Database = Optional<DatabaseSettings.Fragment?>.Present(
                new DatabaseSettings.Fragment
                {
                    Host = Optional<string>.Present("txn-db"),
                    Port = Optional<int>.Present(5434),
                }
            ),
        };

        var write = await source.WriteAsync(new StateWriteRequest<AppSettings.Fragment>(fragment));

        write.Revision.ShouldNotBeNullOrWhiteSpace();
        client.TxnCalls.ShouldBe(1);
        client.PutCalls.ShouldBe(0);
        var read = await source.ReadAsync();
        read.Value!.Enabled.Value.ShouldBeFalse();
        read.Value.RetryCount.Value.ShouldBe(11);
        read.Value.Database.Value!.Host.Value.ShouldBe("txn-db");
    }

    [Test]
    public async Task BatchCapabilityCombinesDisjointPrefixesInOneTransaction()
    {
        var client = new FakeConsulKvClient();
        using var first = CreatePrefixSource(client, "config/a");
        using var second = CreatePrefixSource(client, "config/b");
        var contextA = new ConfiglueResourceContext(
            "model-a",
            new TestSubject("s"),
            ResourceKey.From("s"),
            RouteKey.Default
        );
        var contextB = new ConfiglueResourceContext(
            "model-a",
            new TestSubject("s"),
            ResourceKey.From("s"),
            RouteKey.Default
        );

        var planA = await first.TryCreateBatchWriteAsync(
            contextA,
            new StateWriteRequest<AppSettings.Fragment>(
                new AppSettings.Fragment { Enabled = Optional<bool>.Present(true) }
            )
        );
        var planB = await second.TryCreateBatchWriteAsync(
            contextB,
            new StateWriteRequest<AppSettings.Fragment>(
                new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) }
            )
        );

        planA.ShouldNotBeNull();
        planB.ShouldNotBeNull();
        planA!.ResourceId.ShouldBe(planB!.ResourceId);
        ResourceBatchCompatibility
            .AreCompatible(planA.BatchWriter, planB.BatchWriter, planB.Mutation.Context)
            .ShouldBeTrue();
        ResourceWriteMutation.ValidateBatch([planA.Mutation, planB.Mutation]);

        var beforeTxn = client.TxnCalls;
        var result = await planA.BatchWriter.WriteBatchAsync([planA.Mutation, planB.Mutation]);
        result.Revision.ShouldNotBeNullOrWhiteSpace();
        client.TxnCalls.ShouldBe(beforeTxn + 1);

        planA.Dispose();
        planB.Dispose();
    }

    [Test]
    public async Task ReadOnlySourceExposesNoWriter()
    {
        var client = new FakeConsulKvClient();
        using var source = new ConsulKvSource<AppSettings.Fragment>(
            client,
            AppSettings.ConfiglueSchema,
            "config/app",
            null,
            writable: false
        );

        source.Writer.ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await source.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(new AppSettings.Fragment())
            )
        );
        var plan = await (
            (IAsyncSourceWriteBatchParticipant<AppSettings.Fragment>)source
        ).TryCreateBatchWriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(new AppSettings.Fragment())
        );
        plan.ShouldBeNull();
    }

    [Test]
    public async Task ClientResolverInjectsPerRouteClients()
    {
        var primary = new FakeConsulKvClient();
        var secondary = new FakeConsulKvClient();
        primary.Seed("config/app/enabled", "true");
        secondary.Seed("config/app/enabled", "false");
        var routeA = RouteKey.From("a");
        var routeB = RouteKey.From("b");
        using var source = new ConsulKvSource<AppSettings.Fragment>(
            route => route == routeA ? primary : secondary,
            AppSettings.ConfiglueSchema,
            "config/app"
        );

        var readA = await source.ReadAsync(
            new ConfiglueResourceContext(new TestSubject("s"), ResourceKey.From("s"), routeA)
        );
        var readB = await source.ReadAsync(
            new ConfiglueResourceContext(new TestSubject("s"), ResourceKey.From("s"), routeB)
        );

        readA.Value!.Enabled.Value.ShouldBeTrue();
        readB.Value!.Enabled.Value.ShouldBeFalse();
    }

    [Test]
    public async Task ConsulKeysLayerOverLowerPrioritySources()
    {
        var client = new FakeConsulKvClient();
        client.Seed("config/app/database/host", "consul-db");
        using var consul = CreatePrefixSource(client, "config/app");
        var consulState = new StateSource<AppSettings.Fragment>(
            "consul",
            consul,
            new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }
        );
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Enabled = Optional<bool>.Present(true),
                RetryCount = Optional<int>.Present(5),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("default-db"),
                        Port = Optional<int>.Present(5432),
                    }
                ),
            }
        );
        var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                consulState,
                new StateSource<AppSettings.Fragment>("defaults", defaults, new StateSourceOptions<AppSettings.Fragment> { Priority = 0 }),
            ])
        );

        var resolved = await runtime.ReadAsync();

        resolved.Value!.Database!.Host.ShouldBe("consul-db");
        resolved.Value.Database.Port.ShouldBe(5432);
        resolved.Value.Enabled.ShouldBeTrue();
    }

    [Test]
    public async Task SingleKeyResourceRoundTripsWithCas()
    {
        var client = new FakeConsulKvClient();
        var resource = new ConsulKvResource(client, "config/payload.json");

        var created = await resource.WriteAsync(
            new ResourceWriteRequest(new byte[] { 1, 2, 3 }, RevisionCondition.MustNotExist)
        );
        created.Revision.ShouldNotBeNullOrWhiteSpace();

        var read = await resource.ReadAsync();
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Content.ToArray().ShouldBe(new byte[] { 1, 2, 3 });

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(new byte[] { 9 }, RevisionCondition.MustNotExist)
            )
        );

        var updated = await resource.WriteAsync(
            new ResourceWriteRequest(new byte[] { 4, 5 }, RevisionCondition.Match(read.Revision!))
        );
        updated.Revision.ShouldNotBe(read.Revision);
        (await resource.ReadAsync()).Content.ToArray().ShouldBe(new byte[] { 4, 5 });

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(new byte[] { 6 }, RevisionCondition.Match(read.Revision!))
            )
        );
    }

    [Test]
    public async Task SingleKeyObjectSourceUsesCodecPipeline()
    {
        var client = new FakeConsulKvClient();
        var codec = new JsonStateCodec<string>();
        var resource = new ConsulKvResource(client, "config/greeting");
        var serialized = new SerializedSource<string>(
            resource,
            StateCodecBinding.Typed(codec),
            writer: resource,
            watcher: resource
        );

        await serialized.Writer!.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<string>("hello")
        );
        var read = await serialized.ReadAsync(ConfiglueResourceContext.Default);
        read.Value.ShouldBe("hello");

        // Watch observes the resource revision via a blocking single-key query.
        var watcher = (ISourceWatcher)serialized;
        var revision = read.Revision;
        var watch = watcher.WaitForChangeAsync(ConfiglueResourceContext.Default, revision).AsTask();
        await Task.Delay(50);
        await serialized.Writer!.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<string>("hello-2", RevisionCondition.Match(revision!))
        );
        await watch;
    }

    [Test]
    public void ProvenanceNeverExposesTokens()
    {
        using var httpClient = new HttpClient();
        var secured = new HttpConsulKvClient(httpClient, "http://127.0.0.1:8500", "secret-token");
        secured.ToString().ShouldNotContain("secret-token");

        var client = new FakeConsulKvClient();
        using var source = CreatePrefixSource(client, "config/app");
        var resource = new ConsulKvResource(client, "config/key");
        source.ToString().ShouldNotContain("secret-token");
        resource.ToString().ShouldNotContain("secret-token");
        source
            .GetResourceId(ConfiglueResourceContext.Default)
            .ToString()
            .ShouldNotContain("secret-token");
    }

    [Test]
    public void RegistrationRequiresExactlyOneClient()
    {
        var builder = new ConfiglueSourceSetBuilder();
        var codec = StateCodecBinding.Typed(new JsonStateCodec<string>());
        Should.Throw<ArgumentException>(() =>
            builder.FromConsul(new ConsulKvPrefixSourceOptions { KeyPrefix = "config/app" })
        );
        Should.Throw<ArgumentException>(() =>
            builder.FromConsulObject(
                new ConsulKvObjectSourceOptions
                {
                    Key = "config/key",
                    Codec = codec,
                    Client = new FakeConsulKvClient(),
                    ClientFactory = _ => new FakeConsulKvClient(),
                }
            )
        );
    }

    [Test]
    public void RegistrationFromConsulObjectAcceptsValidOptions()
    {
        var client = new FakeConsulKvClient();
        var codec = StateCodecBinding.Typed(new JsonStateCodec<string>());
        var builder = new ConfiglueSourceSetBuilder();
        var registration = builder.FromConsulObject(
            new ConsulKvObjectSourceOptions
            {
                Id = "consul-object",
                Key = "config/greeting",
                Client = client,
                Codec = codec,
            }
        );

        registration.ShouldNotBeNull();
    }

    private static ConsulKvSource<AppSettings.Fragment> CreatePrefixSource(
        FakeConsulKvClient client,
        string prefix,
        TimeSpan? blockingWait = null
    ) =>
        new(
            client,
            AppSettings.ConfiglueSchema,
            prefix,
            blockingWait is null
                ? null
                : new ConsulKvPrefixOptions { BlockingWaitTimeout = blockingWait.Value }
        );

    private sealed record TestSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }

    private sealed class FakeConsulKvClient : IConsulKvClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, StoredEntry> _store = new(StringComparer.Ordinal);
        private ulong _index = 10;
        private TaskCompletionSource _changed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _failNextList;
        private ulong _modifyCounter = 100;

        public ConsulKvListOptions? LastListOptions { get; private set; }

        public string? LastPrefix { get; private set; }

        public int ListCalls { get; private set; }

        public int PutCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public int TxnCalls { get; private set; }

        public void Seed(string key, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            lock (_gate)
            {
                var modify = ++_modifyCounter;
                _store[key] = new StoredEntry(bytes, modify, modify);
                _index = Math.Max(_index + 1, modify + 1);
                Pulse();
            }
        }

        public void DeleteKey(string key)
        {
            lock (_gate)
            {
                _store.Remove(key);
                _index++;
                Pulse();
            }
        }

        public void FailNextList(int count)
        {
            lock (_gate)
            {
                _failNextList = count;
            }
        }

        public void ResetIndex(ulong value)
        {
            lock (_gate)
            {
                _index = value;
                Pulse();
            }
        }

        public Task<ConsulKvListResult> ListAsync(
            string prefix,
            ConsulKvListOptions? options,
            CancellationToken cancellationToken
        )
        {
            TaskCompletionSource waitFor;
            ulong waitIndex;
            TimeSpan waitTimeout;
            List<ConsulKvEntry> snapshot;
            ulong snapshotIndex;
            lock (_gate)
            {
                ListCalls++;
                LastPrefix = prefix;
                LastListOptions = options;
                if (_failNextList > 0)
                {
                    _failNextList--;
                    throw new HttpRequestException("Simulated Consul reconnect.");
                }

                (snapshot, snapshotIndex) = SnapshotLocked(prefix);
                if (options?.WaitIndex is null || options.WaitIndex < snapshotIndex)
                {
                    return Task.FromResult(new ConsulKvListResult(snapshot, snapshotIndex));
                }

                waitFor = _changed;
                waitIndex = options.WaitIndex.Value;
                waitTimeout = options.WaitTimeout ?? TimeSpan.FromSeconds(5);
            }

            return WaitForChangeAsync(
                prefix,
                snapshot,
                snapshotIndex,
                waitFor,
                waitTimeout,
                cancellationToken
            );
        }

        public Task<ConsulKvListResult> GetAsync(
            string key,
            ConsulKvListOptions? options,
            CancellationToken cancellationToken
        )
        {
            lock (_gate)
            {
                ListCalls++;
                LastPrefix = key;
                LastListOptions = options;
                if (_failNextList > 0)
                {
                    _failNextList--;
                    throw new HttpRequestException("Simulated Consul reconnect.");
                }

                if (_store.TryGetValue(key, out var stored))
                {
                    var entry = new ConsulKvEntry(
                        key,
                        stored.Value,
                        stored.ModifyIndex,
                        stored.CreateIndex
                    );
                    if (options?.WaitIndex is null || options.WaitIndex < _index)
                    {
                        return Task.FromResult(new ConsulKvListResult([entry], _index));
                    }

                    var waiter = _changed;
                    var timeout = options.WaitTimeout ?? TimeSpan.FromSeconds(5);
                    return WaitForSingleAsync(key, waiter, timeout, cancellationToken);
                }

                if (options?.WaitIndex is null || options.WaitIndex < _index)
                {
                    return Task.FromResult(new ConsulKvListResult([], _index));
                }

                var singleWaiter = _changed;
                var singleTimeout = options.WaitTimeout ?? TimeSpan.FromSeconds(5);
                return WaitForSingleAsync(key, singleWaiter, singleTimeout, cancellationToken);
            }
        }

        public Task<(bool Applied, ulong NewIndex)> PutAsync(
            string key,
            ReadOnlyMemory<byte> value,
            ulong? cas,
            ConsulKvWriteOptions? options,
            CancellationToken cancellationToken
        )
        {
            lock (_gate)
            {
                PutCalls++;
                if (cas is { } expected)
                {
                    if (expected == 0)
                    {
                        if (_store.ContainsKey(key))
                        {
                            return Task.FromResult((false, _index));
                        }
                    }
                    else if (
                        !_store.TryGetValue(key, out var existing)
                        || existing.ModifyIndex != expected
                    )
                    {
                        return Task.FromResult((false, _index));
                    }
                }

                var modify = ++_modifyCounter;
                var create = _store.TryGetValue(key, out var prior) ? prior.CreateIndex : modify;
                _store[key] = new StoredEntry(value.ToArray(), modify, create);
                _index = Math.Max(_index + 1, modify + 1);
                Pulse();
                return Task.FromResult((true, _index));
            }
        }

        public Task<(bool Applied, ulong NewIndex)> DeleteAsync(
            string key,
            ulong? cas,
            ConsulKvWriteOptions? options,
            CancellationToken cancellationToken
        )
        {
            lock (_gate)
            {
                DeleteCalls++;
                if (!_store.TryGetValue(key, out var existing))
                {
                    if (cas is 0)
                    {
                        return Task.FromResult((false, _index));
                    }

                    return Task.FromResult((true, _index));
                }

                if (cas is { } expected && expected != 0 && existing.ModifyIndex != expected)
                {
                    return Task.FromResult((false, _index));
                }

                if (cas is 0)
                {
                    return Task.FromResult((false, _index));
                }

                _store.Remove(key);
                _index++;
                Pulse();
                return Task.FromResult((true, _index));
            }
        }

        public Task<ConsulTxnResult> TransactAsync(
            IReadOnlyList<ConsulTxnOperation> operations,
            ConsulKvWriteOptions? options,
            CancellationToken cancellationToken
        )
        {
            lock (_gate)
            {
                TxnCalls++;
                foreach (var operation in operations)
                {
                    if (string.Equals(operation.Verb, "delete", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_store.TryGetValue(operation.Key, out var existing))
                        {
                            if (operation.Cas is 0)
                            {
                                return Task.FromResult(
                                    new ConsulTxnResult(false, _index, ["key does not exist"])
                                );
                            }

                            continue;
                        }

                        if (
                            operation.Cas is { } expected
                            && expected != 0
                            && existing.ModifyIndex != expected
                        )
                        {
                            return Task.FromResult(
                                new ConsulTxnResult(false, _index, ["cas mismatch"])
                            );
                        }

                        if (operation.Cas is 0)
                        {
                            return Task.FromResult(
                                new ConsulTxnResult(false, _index, ["key exists"])
                            );
                        }
                    }
                    else
                    {
                        if (operation.Cas is { } expected)
                        {
                            if (expected == 0)
                            {
                                if (_store.ContainsKey(operation.Key))
                                {
                                    return Task.FromResult(
                                        new ConsulTxnResult(false, _index, ["key exists"])
                                    );
                                }
                            }
                            else if (
                                !_store.TryGetValue(operation.Key, out var existing)
                                || existing.ModifyIndex != expected
                            )
                            {
                                return Task.FromResult(
                                    new ConsulTxnResult(false, _index, ["cas mismatch"])
                                );
                            }
                        }
                    }
                }

                foreach (var operation in operations)
                {
                    if (string.Equals(operation.Verb, "delete", StringComparison.OrdinalIgnoreCase))
                    {
                        _store.Remove(operation.Key);
                    }
                    else
                    {
                        var modify = ++_modifyCounter;
                        var create = _store.TryGetValue(operation.Key, out var prior)
                            ? prior.CreateIndex
                            : modify;
                        _store[operation.Key] = new StoredEntry(
                            operation.Value ?? [],
                            modify,
                            create
                        );
                    }
                }

                _index++;
                Pulse();
                return Task.FromResult(new ConsulTxnResult(true, _index));
            }
        }

        private async Task<ConsulKvListResult> WaitForChangeAsync(
            string prefix,
            List<ConsulKvEntry> immediate,
            ulong immediateIndex,
            TaskCompletionSource waiter,
            TimeSpan timeout,
            CancellationToken cancellationToken
        )
        {
            await Task.WhenAny(waiter.Task, Task.Delay(timeout, cancellationToken))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var (snapshot, index) = SnapshotLocked(prefix);
                return new ConsulKvListResult(snapshot, index);
            }
        }

        private async Task<ConsulKvListResult> WaitForSingleAsync(
            string key,
            TaskCompletionSource waiter,
            TimeSpan timeout,
            CancellationToken cancellationToken
        )
        {
            await Task.WhenAny(waiter.Task, Task.Delay(timeout, cancellationToken))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_store.TryGetValue(key, out var stored))
                {
                    return new ConsulKvListResult(
                        [
                            new ConsulKvEntry(
                                key,
                                stored.Value,
                                stored.ModifyIndex,
                                stored.CreateIndex
                            ),
                        ],
                        _index
                    );
                }

                return new ConsulKvListResult([], _index);
            }
        }

        private (List<ConsulKvEntry> Entries, ulong Index) SnapshotLocked(string prefix)
        {
            var entries = new List<ConsulKvEntry>();
            foreach (var pair in _store)
            {
                if (
                    pair.Key.Equals(prefix, StringComparison.Ordinal)
                    || pair.Key.StartsWith(prefix + "/", StringComparison.Ordinal)
                )
                {
                    entries.Add(
                        new ConsulKvEntry(
                            pair.Key,
                            pair.Value.Value,
                            pair.Value.ModifyIndex,
                            pair.Value.CreateIndex
                        )
                    );
                }
            }

            entries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
            return (entries, _index);
        }

        private void Pulse()
        {
            var previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }

        private sealed record StoredEntry(byte[] Value, ulong ModifyIndex, ulong CreateIndex);
    }
}
