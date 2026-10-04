using Configlue.Provider.Json;
using Configlue.Resource.Etcd;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class EtcdSourceTests
{
    [Test]
    public void MemberPathsEncodeToStableEtcdKeys()
    {
        EtcdKeyEncoding.BuildKey("cfg", "RetryCount").ShouldBe("cfg/RetryCount");
        EtcdKeyEncoding.BuildKey("cfg/", "Database/Host").ShouldBe("cfg/Database/Host");
        EtcdKeyEncoding.BuildKey("cfg", "tenant-a", "Label").ShouldBe("cfg/tenant-a/Label");
        EtcdKeyEncoding.EscapeSegment("a/b").ShouldBe("a%2Fb");
        EtcdKeyEncoding.EscapeSegment("100%").ShouldBe("100%25");
        EtcdKeyEncoding.UnescapeSegment("a%2Fb").ShouldBe("a/b");
        EtcdKeyEncoding.DecodeMemberSuffix("Database/Host").ShouldBe(["Database", "Host"]);
        EtcdKeyEncoding
            .DecodeMemberSuffix(EtcdKeyEncoding.EncodeMemberSuffix(["Database", "Host"]))
            .ShouldBe(["Database", "Host"]);
        EtcdKeyEncoding.TrySplitMemberSuffix("cfg", "cfg/Label", out var suffix).ShouldBeTrue();
        suffix.ShouldBe("Label");
        EtcdKeyEncoding.TrySplitMemberSuffix("cfg", "other/Label", out _).ShouldBeFalse();
    }

    [Test]
    public void RevisionsAreDeterministicAndCarryPerKeyModifications()
    {
        var first = EtcdRevisionCodec.Encode(
            9,
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["cfg/B"] = 4,
                ["cfg/A"] = 7,
            }
        );
        var second = EtcdRevisionCodec.Encode(
            9,
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["cfg/A"] = 7,
                ["cfg/B"] = 4,
            }
        );
        first.ShouldBe(second);
        EtcdRevisionCodec.TryDecode(first, out var header, out var keys).ShouldBeTrue();
        header.ShouldBe(9);
        keys["cfg/A"].ShouldBe(7);
        keys["cfg/B"].ShouldBe(4);
        EtcdRevisionCodec.TryDecode("7:", out var emptyHeader, out var empty).ShouldBeTrue();
        emptyHeader.ShouldBe(7);
        empty.Count.ShouldBe(0);
        EtcdRevisionCodec.TryDecode("garbage", out _, out _).ShouldBeFalse();
        EtcdRevisionCodec.TryDecode("0:", out _, out _).ShouldBeFalse();
    }

    [Test]
    public async Task PrefixReadsContributeNestedMembersWithHeaderRevision()
    {
        using var client = new FakeEtcdClient();
        client.Seed("cfg/Enabled", """false"""u8.ToArray());
        client.Seed("cfg/RetryCount", """7"""u8.ToArray());
        client.Seed("cfg/Database/Host", "db.example.test"u8.ToArray());
        client.Seed("cfg/Database/Port", """5433"""u8.ToArray());
        client.Seed("other/Label", "ignored"u8.ToArray());
        using var source = CreateSource(client, "cfg");

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Enabled.Value.ShouldBeFalse();
        read.Value.RetryCount.Value.ShouldBe(7);
        read.Value.Database.Value!.Host.Value.ShouldBe("db.example.test");
        read.Value.Database.Value.Port.Value.ShouldBe(5433);
        read.Value.Label.IsPresent.ShouldBeFalse();
        read.Schema.ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
        EtcdRevisionCodec.TryDecode(read.Revision, out var header, out var keys).ShouldBeTrue();
        header.ShouldBe(client.CurrentRevision);
        keys.Count.ShouldBe(4);
    }

    [Test]
    public async Task EmptyPrefixReadsAsNotFoundWithWatchableRevision()
    {
        using var client = new FakeEtcdClient();
        using var source = CreateSource(client, "cfg");

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.NotFound);
        read.Revision.ShouldBe("1:");
    }

    [Test]
    public async Task DeleteEventsRemoveMembersOnNextRead()
    {
        using var client = new FakeEtcdClient();
        client.Seed("cfg/RetryCount", """3"""u8.ToArray());
        client.Seed("cfg/Label", "before"u8.ToArray());
        using var source = CreateSource(client, "cfg");
        var observed = (await source.ReadAsync()).Revision;

        client.Delete("cfg/Label");
        client.EnqueueBehavior(
            new EtcdWatchResponse(
                [
                    new EtcdWatchEvent(
                        EtcdWatchEventKind.Delete,
                        "cfg/Label",
                        ReadOnlyMemory<byte>.Empty,
                        client.CurrentRevision
                    ),
                ],
                client.CurrentRevision,
                IsProgressNotification: false
            )
        );

        await source.WaitForChangeAsync(observed!);

        var reread = await source.ReadAsync();
        reread.Value!.Label.IsPresent.ShouldBeFalse();
        reread.Revision.ShouldNotBe(observed);
    }

    [Test]
    public async Task WatchResumesFromObservedRevisionAfterTransientFailure()
    {
        using var client = new FakeEtcdClient();
        client.Seed("cfg/RetryCount", """1"""u8.ToArray());
        using var source = CreateSource(
            client,
            "cfg",
            new EtcdResourceOptions { KeyPrefix = "cfg", ReconnectDelay = TimeSpan.Zero }
        );
        var observed = (await source.ReadAsync()).Revision;
        client.EnqueueBehavior(new EtcdTransientException("flaky"));
        client.EnqueueBehavior(
            new EtcdWatchResponse([], client.CurrentRevision, IsProgressNotification: true)
        );
        client.EnqueueBehavior(
            new EtcdWatchResponse(
                [
                    new EtcdWatchEvent(
                        EtcdWatchEventKind.Put,
                        "cfg/RetryCount",
                        """2"""u8.ToArray(),
                        client.CurrentRevision
                    ),
                ],
                client.CurrentRevision,
                IsProgressNotification: false
            )
        );

        await source.WaitForChangeAsync(observed!);

        client.WatchCalls.Count.ShouldBe(2);
        client.WatchCalls[0].Prefix.ShouldBe("cfg/");
        client.WatchCalls[1].StartRevision.ShouldBe(client.WatchCalls[0].StartRevision);
    }

    [Test]
    public async Task WatchCompactionForcesFreshReadAndConverges()
    {
        using var client = new FakeEtcdClient();
        client.Seed("cfg/RetryCount", """1"""u8.ToArray());
        using var source = CreateSource(
            client,
            "cfg",
            new EtcdResourceOptions { KeyPrefix = "cfg", ReconnectDelay = TimeSpan.Zero }
        );
        var observed = (await source.ReadAsync()).Revision;
        var rangesBefore = client.RangeCalls;
        client.EnqueueBehavior(new EtcdCompactedException(2));

        await source.WaitForChangeAsync(observed!);

        client.RangeCalls.ShouldBe(rangesBefore + 2);
        client.WatchCalls.Count.ShouldBe(1);

        client.Seed("cfg/RetryCount", """9"""u8.ToArray());
        var reread = await source.ReadAsync();
        reread.Value!.RetryCount.Value.ShouldBe(9);
    }

    [Test]
    public async Task StaleBaselineWritesBecomeConflicts()
    {
        using var client = new FakeEtcdClient();
        client.Seed("cfg/RetryCount", """1"""u8.ToArray());
        using var source = CreateSource(client, "cfg");
        var stale = (await source.ReadAsync()).Revision;
        client.Seed("cfg/RetryCount", """2"""u8.ToArray());

        await Should.ThrowAsync<StateConflictException>(async () =>
            await source.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(
                    FragmentWithRetryCount(3),
                    RevisionCondition.Match(stale!)
                )
            )
        );

        client.Transactions.Count.ShouldBe(1);
        client.Transactions[0].Response.ShouldBeFalse();
    }

    [Test]
    public async Task MalformedRevisionTokensConflictWithoutTransactions()
    {
        using var client = new FakeEtcdClient();
        using var source = CreateSource(client, "cfg");

        await Should.ThrowAsync<StateConflictException>(async () =>
            await source.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(
                    FragmentWithRetryCount(3),
                    RevisionCondition.Match("not-a-revision")
                )
            )
        );

        client.Transactions.Count.ShouldBe(0);
    }

    [Test]
    public async Task OneFragmentWriteCommitsAllKeysInOneTransaction()
    {
        using var client = new FakeEtcdClient();
        using var source = CreateSource(client, "cfg");

        var result = await source.WriteAsync(
            new StateWriteRequest<AppSettings.Fragment>(FullFragment())
        );

        client.Transactions.Count.ShouldBe(1);
        var recorded = client.Transactions[0];
        recorded.Writes.Count.ShouldBe(3);
        recorded.Compares.Count.ShouldBe(0);
        recorded
            .Writes.Select(static write => write.Key)
            .OrderBy(static key => key)
            .ShouldBe(["cfg/Database/Host", "cfg/Enabled", "cfg/RetryCount"]);
        EtcdRevisionCodec.TryDecode(result.Revision, out _, out var keys).ShouldBeTrue();
        keys.Count.ShouldBe(3);

        var read = await source.ReadAsync();
        read.Value!.RetryCount.Value.ShouldBe(7);
        read.Value.Enabled.Value.ShouldBeFalse();
        read.Value.Database.Value!.Host.Value.ShouldBe("db.example.test");
    }

    [Test]
    public async Task OmittedMembersAreDeletedWithAMatchingBaseline()
    {
        using var client = new FakeEtcdClient();
        client.Seed("cfg/RetryCount", """7"""u8.ToArray());
        client.Seed("cfg/Label", "removable"u8.ToArray());
        using var source = CreateSource(client, "cfg");
        var baseline = (await source.ReadAsync()).Revision;

        await source.WriteAsync(
            new StateWriteRequest<AppSettings.Fragment>(
                FragmentWithRetryCount(8),
                RevisionCondition.Match(baseline!)
            )
        );

        var recorded = client.Transactions[^1];
        recorded.Writes.OfType<EtcdDelete>().Select(static write => write.Key).ShouldBe(["cfg/Label"]);
        var read = await source.ReadAsync();
        read.Value!.RetryCount.Value.ShouldBe(8);
        read.Value.Label.IsPresent.ShouldBeFalse();
    }

    [Test]
    public async Task BatchPlansFromTwoSubjectsCommitInOneTransaction()
    {
        using var client = new FakeEtcdClient();
        using var source = CreateSource(client, "cfg");
        var firstContext = SubjectContext("tenant-a");
        var secondContext = SubjectContext("tenant-b");
        var firstRead = await source.ReadAsync(firstContext);
        var secondRead = await source.ReadAsync(secondContext);
        firstRead.Revision.ShouldBe(secondRead.Revision);

        var firstPlan = await source.TryCreateBatchWriteAsync(
            firstContext,
            new StateWriteRequest<AppSettings.Fragment>(
                FragmentWithRetryCount(1),
                RevisionCondition.MustNotExist
            )
        );
        var secondPlan = await source.TryCreateBatchWriteAsync(
            secondContext,
            new StateWriteRequest<AppSettings.Fragment>(
                FragmentWithRetryCount(2),
                RevisionCondition.MustNotExist
            )
        );

        firstPlan.ShouldNotBeNull();
        secondPlan.ShouldNotBeNull();
        firstPlan!.ResourceId.ShouldBe(secondPlan!.ResourceId);
        var writer = firstPlan.BatchWriter;
        ReferenceEquals(writer, secondPlan.BatchWriter).ShouldBeTrue();
        var batchResult = await writer.WriteBatchAsync(
            [firstPlan.Mutation, secondPlan.Mutation]
        );

        client.Transactions.Count.ShouldBe(1);
        var expectedKeys = new[]
        {
            "cfg/" + EtcdKeyEncoding.EscapeSegment(ResourceKey.From(new TestSubject("tenant-a").Key).Value) + "/RetryCount",
            "cfg/" + EtcdKeyEncoding.EscapeSegment(ResourceKey.From(new TestSubject("tenant-b").Key).Value) + "/RetryCount",
        };
        client
            .Transactions[0]
            .Writes.Select(static write => write.Key)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ShouldBe(expectedKeys.OrderBy(static key => key, StringComparer.Ordinal));
        (await source.ReadAsync(firstContext)).Value!.RetryCount.Value.ShouldBe(1);
        (await source.ReadAsync(secondContext)).Value!.RetryCount.Value.ShouldBe(2);
        batchResult.Revision.ShouldNotBeNull();
        firstPlan.Dispose();
        secondPlan.Dispose();
    }

    [Test]
    public async Task CancellationAndDisposalAreDeterministic()
    {
        using var client = new FakeEtcdClient();
        using var source = CreateSource(client, "cfg");
        var observed = (await source.ReadAsync()).Revision;
        client.EnqueueHang();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await source.WaitForChangeAsync(observed!, cancelled.Token)
        );

        var pending = source.WaitForChangeAsync(observed!);
        source.Dispose();
        await pending;

        await Should.ThrowAsync<ObjectDisposedException>(async () => await source.ReadAsync());
        client.Disposed.ShouldBeFalse();
    }

    [Test]
    public void TransportOptionsCarryCredentialsWithoutExposure()
    {
        var options = new EtcdResourceOptions
        {
            KeyPrefix = "cfg",
            Endpoints = ["https://etcd.example.test:2379"],
            Tls = new EtcdTlsOptions
            {
                CaCertificatePem = "ca-secret",
                ClientCertificatePem = "client-secret",
                ClientKeyPem = "key-secret",
            },
            Auth = new EtcdAuthOptions { Username = "configlue" },
        };
        options.Validate();

        options.Tls.ToString().ShouldNotContain("secret");
        options.Auth.ToString().ShouldNotContain("configlue");
        Should.Throw<ArgumentException>(() =>
            new EtcdResourceOptions { KeyPrefix = "   " }.Validate()
        );
        Should.Throw<ArgumentException>(() =>
            new EtcdResourceOptions { KeyPrefix = "cfg", Endpoints = [] }.Validate()
        );
    }

    [Test]
    public async Task InjectedClientsAreUsedPerRouteWithoutTakingOwnership()
    {
        using var primary = new FakeEtcdClient();
        using var secondary = new FakeEtcdClient();
        primary.Seed("cfg/RetryCount", """1"""u8.ToArray());
        secondary.Seed("cfg/RetryCount", """2"""u8.ToArray());
        using var source = new EtcdSource<AppSettings.Fragment>(
            AppSettings.ConfiglueSchema,
            route => route == RouteKey.From("secondary") ? secondary : primary,
            new EtcdResourceOptions { KeyPrefix = "cfg" }
        );

        var primaryRead = await source.ReadAsync(DefaultContext(RouteKey.Default));
        var secondaryRead = await source.ReadAsync(
            DefaultContext(RouteKey.From("secondary"))
        );

        primaryRead.Value!.RetryCount.Value.ShouldBe(1);
        secondaryRead.Value!.RetryCount.Value.ShouldBe(2);
        source.GetResourceId(DefaultContext(RouteKey.Default)).ShouldNotBe(
            source.GetResourceId(DefaultContext(RouteKey.From("secondary")))
        );
        source.Dispose();
        primary.Disposed.ShouldBeFalse();
        secondary.Disposed.ShouldBeFalse();
    }

    [Test]
    public async Task MalformedValuesReportInvalidPayloadAndUnknownKeysAreIgnored()
    {
        using var client = new FakeEtcdClient();
        client.Seed("cfg/RetryCount", "not-an-int"u8.ToArray());
        client.Seed("cfg/Unmapped", "ignored"u8.ToArray());
        using var source = CreateSource(client, "cfg");

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.InvalidPayload);
        read.Revision.ShouldNotBeNull();
    }

    [Test]
    public async Task LayeredSourcesMergeEtcdContributionsWithDefaults()
    {
        using var client = new FakeEtcdClient();
        client.Seed("cfg/RetryCount", """7"""u8.ToArray());
        using var etcd = CreateSource(client, "cfg");
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Label = Optional<string?>.Present("default-label"),
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
            new StateSourceSet<AppSettings.Fragment>(
                [
                    new StateSource<AppSettings.Fragment>("etcd", etcd, new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
                    new StateSource<AppSettings.Fragment>("defaults", defaults, new StateSourceOptions<AppSettings.Fragment> { Priority = 0 }),
                ]
            )
        );

        var resolved = await runtime.ReadAsync();

        resolved.Value!.RetryCount.ShouldBe(7);
        resolved.Value.Label.ShouldBe("default-label");
        resolved.Value.Database!.Host.ShouldBe("default-db");
    }

    [Test]
    public async Task SerializedSingleKeyModeReusesResourceAndCodec()
    {
        using var client = new FakeEtcdClient();
        using var resource = new EtcdResource(client, new EtcdResourceOptions { KeyPrefix = "objects" });
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var serialized = new SerializedSource<AppSettings.Fragment>(
            resource,
            StateCodecBinding.Typed(codec),
            writer: resource
        );

        await serialized.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(
                FullFragment(),
                RevisionCondition.MustNotExist
            )
        );
        var read = await serialized.ReadAsync();
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.RetryCount.Value.ShouldBe(7);

        await Should.ThrowAsync<StateConflictException>(async () =>
            await serialized.WriteAsync(
                ConfiglueResourceContext.Default,
                new StateWriteRequest<AppSettings.Fragment>(
                    FragmentWithRetryCount(9),
                    RevisionCondition.MustNotExist
                )
            )
        );

        var current = await serialized.ReadAsync();
        var updated = await serialized.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(
                FragmentWithRetryCount(9),
                RevisionCondition.Match(current.Revision!)
            )
        );
        updated.Revision.ShouldNotBeNull();
        (await serialized.ReadAsync()).Value!.RetryCount.Value.ShouldBe(9);
    }

    [Test]
    public async Task SourceRegistrationBuildsModelBoundSources()
    {
        using var client = new FakeEtcdClient();
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromEtcd(
                        new EtcdStateSourceOptions
                        {
                            Id = "etcd",
                            Client = client,
                            ResourceOptions = new EtcdResourceOptions { KeyPrefix = "cfg" },
                        }
                    );
                    sources.FromEtcdObject(
                        new EtcdObjectSourceOptions
                        {
                            Id = "etcd-object",
                            Client = client,
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                            ResourceOptions = new EtcdResourceOptions { KeyPrefix = "objects" },
                            Writable = false,
                        }
                    );
                })
            );
        });

        var sources = context.GetRuntimeState<AppSettings>().GetDiagnostics().Sources;
        sources.Count.ShouldBe(2);
        sources.Select(static source => source.Id).Distinct().Count().ShouldBe(2);
    }

    private static EtcdSource<AppSettings.Fragment> CreateSource(
        FakeEtcdClient client,
        string prefix,
        EtcdResourceOptions? options = null
    ) =>
        new(
            AppSettings.ConfiglueSchema,
            _ => client,
            _ => client,
            options ?? new EtcdResourceOptions { KeyPrefix = prefix }
        );

    private static AppSettings.Fragment FragmentWithRetryCount(int retryCount) =>
        new() { RetryCount = Optional<int>.Present(retryCount) };

    private static AppSettings.Fragment FullFragment() =>
        new()
        {
            Enabled = Optional<bool>.Present(false),
            RetryCount = Optional<int>.Present(7),
            Database = Optional<DatabaseSettings.Fragment?>.Present(
                new DatabaseSettings.Fragment
                {
                    Host = Optional<string>.Present("db.example.test"),
                }
            ),
        };

    private static ConfiglueResourceContext SubjectContext(string subject)
    {
        var subjectValue = new TestSubject(subject);
        return new ConfiglueResourceContext(
            subjectValue,
            ResourceKey.From(subjectValue.Key),
            RouteKey.Default
        );
    }

    private static ConfiglueResourceContext DefaultContext(RouteKey route) =>
        new(TestSubject.Default, ResourceKey.Default, route);

    private sealed record TestSubject(string Name) : IConfiglueSubject
    {
        public static TestSubject Default { get; } = new("default");

        public SubjectKey Key => SubjectKey.From(Name);
    }

    private sealed class FakeEtcdClient : IEtcdClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Stored> _store = new(StringComparer.Ordinal);
        private readonly Queue<object> _watchScript = new();
        private long _revision = 1;

        public List<(string Prefix, long? StartRevision)> WatchCalls { get; } = [];

        public List<RecordedTxn> Transactions { get; } = [];

        public int RangeCalls { get; private set; }

        public bool Disposed { get; private set; }

        public long CurrentRevision
        {
            get
            {
                lock (_gate)
                {
                    return _revision;
                }
            }
        }

        public void Seed(string key, byte[] value)
        {
            lock (_gate)
            {
                _revision++;
                var create = _store.TryGetValue(key, out var existing)
                    ? existing.CreateRevision
                    : _revision;
                _store[key] = new Stored(value, _revision, create);
            }
        }

        public void Delete(string key)
        {
            lock (_gate)
            {
                if (_store.Remove(key))
                {
                    _revision++;
                }
            }
        }

        public void EnqueueBehavior(object behavior)
        {
            lock (_gate)
            {
                _watchScript.Enqueue(behavior);
            }
        }

        public void EnqueueHang() => EnqueueBehavior(Hang.Instance);

        public ValueTask<EtcdRangeResponse> GetPrefixAsync(
            string prefix,
            long? revision = null,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                RangeCalls++;
                var matches = _store
                    .Where(pair =>
                        pair.Key.Equals(prefix, StringComparison.Ordinal)
                        || pair.Key.StartsWith(prefix, StringComparison.Ordinal)
                    )
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => new EtcdKeyValue(
                        pair.Key,
                        pair.Value.Value,
                        pair.Value.ModRevision,
                        pair.Value.CreateRevision
                    ))
                    .ToArray();
                return ValueTaskCompat.FromResult(new EtcdRangeResponse(matches, _revision));
            }
        }

        public ValueTask<EtcdTxnResponse> TransactAsync(
            IReadOnlyList<EtcdCompare> compares,
            IReadOnlyList<EtcdWrite> writes,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var succeeded = compares.All(compare =>
                    compare.Kind == EtcdCompareKind.KeyNotExists
                        ? !_store.ContainsKey(compare.Key)
                        : _store.TryGetValue(compare.Key, out var current)
                            && current.ModRevision == compare.ExpectedModRevision
                );
                if (succeeded)
                {
                    _revision++;
                    foreach (var write in writes)
                    {
                        if (write is EtcdDelete)
                        {
                            _store.Remove(write.Key);
                            continue;
                        }

                        var put = (EtcdPut)write;
                        var create = _store.TryGetValue(write.Key, out var existing)
                            ? existing.CreateRevision
                            : _revision;
                        _store[write.Key] = new Stored(put.Value.ToArray(), _revision, create);
                    }
                }

                var recorded = new RecordedTxn(
                    compares.ToArray(),
                    writes.ToArray(),
                    succeeded,
                    _revision
                );
                Transactions.Add(recorded);
                return ValueTaskCompat.FromResult(
                    new EtcdTxnResponse(succeeded, _revision)
                );
            }
        }

        public async Task WatchPrefixAsync(
            string prefix,
            long? startRevision,
            Func<EtcdWatchResponse, CancellationToken, ValueTask<bool>> onResponse,
            CancellationToken cancellationToken = default
        )
        {
            lock (_gate)
            {
                WatchCalls.Add((prefix, startRevision));
            }

            while (true)
            {
                object? behavior;
                lock (_gate)
                {
                    _watchScript.TryDequeue(out behavior);
                }

                switch (behavior)
                {
                    case null:
                    case Hang:
                        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                        return;
                    case Exception failure:
                        throw failure;
                    case EtcdWatchResponse response:
                        if (await onResponse(response, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        break;
                    default:
                        throw new InvalidOperationException("Unknown watch behavior.");
                }
            }
        }

        public void Dispose() => Disposed = true;

        private sealed record Stored(byte[] Value, long ModRevision, long CreateRevision);

        private sealed class Hang
        {
            public static Hang Instance { get; } = new();
        }
    }

    internal sealed record RecordedTxn(
        IReadOnlyList<EtcdCompare> Compares,
        IReadOnlyList<EtcdWrite> Writes,
        bool Response,
        long Revision
    );
}
