using Azure.Core;
using Azure.Data.AppConfiguration;
using Configlue.Resource.AzureAppConfiguration;

namespace Configlue.Tests;

public sealed class AzureAppConfigurationTests
{
    [Test]
    public void KeyMapper_TrimPrefixAndNestingAreDeterministic()
    {
        AzureAppConfigurationKeyMapper
            .TryMapToMemberPath("App:Database:Host", "App:", out var path)
            .ShouldBeTrue();
        path.ShouldBe(["Database", "Host"]);
        AzureAppConfigurationKeyMapper
            .ToConfigurationKey(path, "App:")
            .ShouldBe("App:Database:Host");

        AzureAppConfigurationKeyMapper
            .TryMapToMemberPath("App/Database/Port", "App/", out var slashPath)
            .ShouldBeTrue();
        slashPath.ShouldBe(["Database", "Port"]);

        AzureAppConfigurationKeyMapper
            .TryMapToMemberPath("Other:Key", "App:", out _)
            .ShouldBeFalse();

        AzureAppConfigurationKeyMapper
            .MatchesKeyFilter("App:Database:Host", "App:*")
            .ShouldBeTrue();
        AzureAppConfigurationKeyMapper.MatchesKeyFilter("Other:Key", "App:*").ShouldBeFalse();
        AzureAppConfigurationKeyMapper.MatchesKeyFilter("Any:Key", "*").ShouldBeTrue();
        AzureAppConfigurationKeyMapper.MatchesKeyFilter("App:A", "App:A,App:B").ShouldBeTrue();
        AzureAppConfigurationKeyMapper.MatchesKeyFilter("App:C", "App:A,App:B").ShouldBeFalse();

        AzureAppConfigurationKeyMapper
            .IsFeatureFlagKey(".appconfig.featureflag/my-flag")
            .ShouldBeTrue();
        AzureAppConfigurationKeyMapper
            .IsFeatureFlagContentType(AzureAppConfigurationKeyMapper.FeatureFlagContentType)
            .ShouldBeTrue();
        AzureAppConfigurationKeyMapper
            .IsKeyVaultReference(AzureAppConfigurationKeyMapper.KeyVaultReferenceContentType)
            .ShouldBeTrue();

        Should.Throw<FormatException>(() =>
            AzureAppConfigurationKeyMapper.TryMapToMemberPath("App:", "App:", out _)
        );
        Should.Throw<FormatException>(() =>
            AzureAppConfigurationKeyMapper.TryMapToMemberPath("App:Database::Host", "App:", out _)
        );
    }

    [Test]
    public async Task Read_MapsSelectedKeysWithTrimAndNesting()
    {
        var fake = new FakeAppConfigurationClient();
        fake.Upsert("App:Enabled", null, "false", null);
        fake.Upsert("App:RetryCount", null, "17", null);
        fake.Upsert("App:Database:Host", null, "db.local", null);
        fake.Upsert("App:Database:Port", null, "6432", null);
        fake.Upsert("Other:Ignored", null, "x", null);
        using var source = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions
            {
                Client = DummyClient(),
                KeyFilter = "App:*",
                TrimKeyPrefix = "App:",
            }
        );

        var first = await source.ReadAsync();
        var second = await source.ReadAsync();

        first.Status.ShouldBe(StateReadStatus.Success);
        first.Value!.Enabled.Value.ShouldBeFalse();
        first.Value.RetryCount.Value.ShouldBe(17);
        first.Value.Label.IsPresent.ShouldBeFalse();
        first.Value.Database.IsPresent.ShouldBeTrue();
        first.Value.Database.Value!.Host.Value.ShouldBe("db.local");
        first.Value.Database.Value.Port.Value.ShouldBe(6432);
        second.Revision.ShouldBe(first.Revision);
    }

    [Test]
    public async Task Read_RespectsLabelFilter()
    {
        var fake = new FakeAppConfigurationClient();
        fake.Upsert("App:Label", null, "unlabeled", null);
        fake.Upsert("App:Label", "prod", "produced", null);
        using var unlabeled = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions
            {
                Client = DummyClient(),
                KeyFilter = "App:*",
                TrimKeyPrefix = "App:",
                LabelFilter = null,
            }
        );
        using var prod = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions
            {
                Client = DummyClient(),
                KeyFilter = "App:*",
                TrimKeyPrefix = "App:",
                LabelFilter = "prod",
            }
        );

        (await unlabeled.ReadAsync()).Value!.Label.Value.ShouldBe("unlabeled");
        (await prod.ReadAsync()).Value!.Label.Value.ShouldBe("produced");
    }

    [Test]
    public async Task Read_ExcludesFeatureFlags()
    {
        var fake = new FakeAppConfigurationClient();
        fake.Upsert(
            ".appconfig.featureflag/my-flag",
            null,
            """{"id":"my-flag"}""",
            AzureAppConfigurationKeyMapper.FeatureFlagContentType
        );
        fake.Upsert("App:RetryCount", null, "7", null);
        using var source = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions
            {
                Client = DummyClient(),
                KeyFilter = "*",
                TrimKeyPrefix = "App:",
            }
        );

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.RetryCount.Value.ShouldBe(7);

        var flagsOnly = new FakeAppConfigurationClient();
        flagsOnly.Upsert(
            ".appconfig.featureflag/only",
            null,
            """{"id":"only"}""",
            AzureAppConfigurationKeyMapper.FeatureFlagContentType
        );
        using var empty = CreateSource(
            flagsOnly,
            new AzureAppConfigurationSourceOptions { Client = DummyClient(), KeyFilter = "*" }
        );

        (await empty.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task Read_PreservesKeyVaultReferencesAsOpaqueValues()
    {
        var reference = """{"uri":"https://vault.test/secrets/db-password"}""";
        var fake = new FakeAppConfigurationClient();
        fake.Upsert(
            "App:Label",
            null,
            reference,
            AzureAppConfigurationKeyMapper.KeyVaultReferenceContentType
        );
        using var source = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions
            {
                Client = DummyClient(),
                KeyFilter = "App:*",
                TrimKeyPrefix = "App:",
            }
        );

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Label.Value.ShouldBe(reference);
    }

    [Test]
    public async Task Write_WithMatchingETagSucceedsAndStaleFails()
    {
        var fake = new FakeAppConfigurationClient();
        fake.Upsert("App:RetryCount", null, "1", null);
        using var source = CreateSource(fake, Writable(options => options));

        var first = await source.ReadAsync();
        var updated = await source.WriteAsync(
            new StateWriteRequest<AppSettings.Fragment>(
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) },
                RevisionCondition.Match(first.Revision!)
            )
        );
        updated.Revision.ShouldNotBe(first.Revision);
        (await source.ReadAsync()).Value!.RetryCount.Value.ShouldBe(2);

        await Should.ThrowAsync<StateConflictException>(async () =>
            await source.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) },
                    RevisionCondition.Match(first.Revision!)
                )
            )
        );
    }

    [Test]
    public async Task Write_FakeEnforcesPerKeyETagConditionals()
    {
        var fake = new FakeAppConfigurationClient();
        fake.Upsert("App:RetryCount", null, "1", null);

        await Should.ThrowAsync<StateConflictException>(async () =>
            await fake.SetSettingAsync(
                "App:RetryCount",
                "2",
                null,
                null,
                AppConfigurationWriteCondition.Match("\"stale\""),
                CancellationToken.None
            )
        );
        await Should.ThrowAsync<StateConflictException>(async () =>
            await fake.SetSettingAsync(
                "App:RetryCount",
                "2",
                null,
                null,
                AppConfigurationWriteCondition.MustNotExist,
                CancellationToken.None
            )
        );
    }

    [Test]
    public void ReadOnly_ByDefaultAndSnapshotModeRejectsWrites()
    {
        var fake = new FakeAppConfigurationClient();
        using var readOnly = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions { Client = DummyClient() }
        );

        readOnly.Writer.ShouldBeNull();
    }

    [Test]
    public async Task Write_ReadOnlySourceThrows()
    {
        var fake = new FakeAppConfigurationClient();
        using var readOnly = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions { Client = DummyClient() }
        );

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await readOnly.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(new AppSettings.Fragment())
            )
        );
    }

    [Test]
    public async Task Snapshot_IsAnExplicitReadOnlyMode()
    {
        var fake = new FakeAppConfigurationClient();
        fake.Upsert("App:RetryCount", null, "1", null);
        fake.CreateSnapshot("v1");
        fake.Upsert("App:RetryCount", null, "2", null);
        using var snapshot = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions
            {
                Client = DummyClient(),
                KeyFilter = "App:*",
                TrimKeyPrefix = "App:",
                SnapshotName = "v1",
            }
        );

        snapshot.Writer.ShouldBeNull();
        (await snapshot.ReadAsync()).Value!.RetryCount.Value.ShouldBe(1);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await snapshot.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) }
                )
            )
        );
    }

    [Test]
    public async Task Watcher_SelectedKeysModeInvalidatesOnChange()
    {
        var fake = new FakeAppConfigurationClient();
        fake.Upsert("App:RetryCount", null, "1", null);
        using var source = CreateSource(
            fake,
            Writable(options => options, refresh: TimeSpan.FromMilliseconds(20))
        );

        var observed = (await source.ReadAsync()).Revision;
        var wait = source.WaitForChangeAsync(observed).AsTask();
        await Task.Delay(150);
        wait.IsCompleted.ShouldBeFalse();

        fake.Upsert("App:RetryCount", null, "2", null);
        await wait.WaitAsync(TimeSpan.FromSeconds(30));

        var refreshed = await source.ReadAsync();
        refreshed.Value!.RetryCount.Value.ShouldBe(2);
        refreshed.Revision.ShouldNotBe(observed);
    }

    [Test]
    public async Task Watcher_SentinelModeCoordinatesPublication()
    {
        var fake = new FakeAppConfigurationClient();
        fake.Upsert("App:RetryCount", null, "1", null);
        fake.Upsert("App:Sentinel", null, "v1", null);
        using var source = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions
            {
                Client = DummyClient(),
                KeyFilter = "App:*",
                TrimKeyPrefix = "App:",
                SentinelKey = "App:Sentinel",
                RefreshInterval = TimeSpan.FromMilliseconds(20),
                Writable = true,
            }
        );

        var observed = (await source.ReadAsync()).Revision;
        var wait = source.WaitForChangeAsync(observed).AsTask();

        fake.Upsert("App:RetryCount", null, "2", null);
        (await Task.WhenAny(wait, Task.Delay(300))).ShouldNotBe(wait);

        fake.Upsert("App:Sentinel", null, "v2", null);
        await wait.WaitAsync(TimeSpan.FromSeconds(30));

        var reread = await source.ReadAsync();
        reread.Value!.RetryCount.Value.ShouldBe(2);
    }

    [Test]
    public async Task TransientFailuresPropagate()
    {
        var fake = new FakeAppConfigurationClient
        {
            ReadFault = () => new InvalidOperationException("transient"),
        };
        using var source = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions { Client = DummyClient() }
        );

        await Should.ThrowAsync<InvalidOperationException>(async () => await source.ReadAsync());
    }

    [Test]
    public async Task CancellationAndDisposalBehave()
    {
        var fake = new FakeAppConfigurationClient();
        fake.Upsert("App:RetryCount", null, "1", null);
        using var source = CreateSource(
            fake,
            Writable(options => options, refresh: TimeSpan.FromMilliseconds(20))
        );

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await source.ReadAsync(cancellationToken: canceled.Token)
        );
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await source.WaitForChangeAsync("revision", canceled.Token)
        );

        var observed = (await source.ReadAsync()).Revision;
        var disposable = CreateSource(
            fake,
            Writable(options => options, refresh: TimeSpan.FromMilliseconds(20))
        );
        var wait = disposable.WaitForChangeAsync(observed).AsTask();
        disposable.Dispose();
        await wait.WaitAsync(TimeSpan.FromSeconds(30));
        await Should.ThrowAsync<ObjectDisposedException>(async () => await disposable.ReadAsync());
    }

    [Test]
    public void Registration_ValidatesInjectionContracts()
    {
        var builder = new ConfiglueSourceSetBuilder();
        Should.Throw<ArgumentException>(() =>
            builder.FromAzureAppConfiguration(new AzureAppConfigurationSourceOptions())
        );
        Should.Throw<ArgumentException>(() =>
            builder.FromAzureAppConfiguration(
                new AzureAppConfigurationSourceOptions
                {
                    Client = DummyClient(),
                    Endpoint = new Uri("https://example.azconfig.io"),
                }
            )
        );
        Should.Throw<ArgumentException>(() =>
            builder.FromAzureAppConfiguration(
                new AzureAppConfigurationSourceOptions
                {
                    Endpoint = new Uri("https://example.azconfig.io"),
                }
            )
        );
        Should.Throw<ArgumentException>(() =>
            builder.FromAzureAppConfiguration(
                new AzureAppConfigurationSourceOptions
                {
                    Client = DummyClient(),
                    SnapshotName = "v1",
                    Writable = true,
                }
            )
        );
    }

    [Test]
    public async Task Registration_SupportsClientFactoryInjectionAndRedactsSecrets()
    {
        var endpoint = new Uri("https://example.azconfig.io");
        var factoryCalls = 0;
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromAzureAppConfiguration(
                        new AzureAppConfigurationSourceOptions
                        {
                            Endpoint = endpoint,
                            CredentialFactory = _ =>
                            {
                                factoryCalls++;
                                return new FakeCredential();
                            },
                            KeyFilter = "App:*",
                        }
                    );
                })
            );
        });

        factoryCalls.ShouldBe(1);
        var diagnostics = context.GetRuntimeState<AppSettings>().GetDiagnostics();
        diagnostics.Sources.Count.ShouldBe(1);
        (diagnostics.Sources[0].PhysicalOrigin ?? string.Empty).ShouldContain(
            "example.azconfig.io"
        );
        (diagnostics.Sources[0].PhysicalOrigin ?? string.Empty).ShouldNotContain("secret");
    }

    [Test]
    public void ResourceId_IsStableAndNeverContainsSecrets()
    {
        var fake = new FakeAppConfigurationClient();
        const string secret =
            "Endpoint=https://example.azconfig.io;Id=redacted;Secret=super-secret-value";
        var viaConnectionString = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions { ConnectionString = secret }
        );
        var same = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions { ConnectionString = secret }
        );
        var different = CreateSource(
            fake,
            new AzureAppConfigurationSourceOptions
            {
                ConnectionString = secret,
                KeyFilter = "Other:*",
            }
        );

        var context = ConfiglueResourceContext.Default;
        viaConnectionString.GetResourceId(context).ShouldBe(same.GetResourceId(context));
        viaConnectionString.GetResourceId(context).ShouldNotBe(different.GetResourceId(context));
        viaConnectionString.GetResourceId(context).Value.ShouldNotContain("super-secret-value");
        viaConnectionString.GetResourceId(context).Value.ShouldNotContain("Secret");
    }

    [Test]
    public async Task MultiKeyWritesAreDocumentedAsNonTransactional()
    {
        var fake = new FakeAppConfigurationClient();
        fake.Upsert("App:Enabled", null, "true", null);
        using var source = CreateSource(fake, Writable(options => options));

        ((object)source is IAsyncSourceWriteBatchParticipant<AppSettings.Fragment>).ShouldBeFalse();

        fake.FailKeys.Add("App:RetryCount");
        var observed = (await source.ReadAsync()).Revision;
        await Should.ThrowAsync<StateConflictException>(async () =>
            await source.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment
                    {
                        Enabled = Optional<bool>.Present(false),
                        RetryCount = Optional<int>.Present(9),
                    },
                    RevisionCondition.Match(observed!)
                )
            )
        );

        (await source.ReadAsync()).Value!.Enabled.Value.ShouldBeFalse();
    }

    [Test]
    public async Task Layering_MergesPrioritizedSources()
    {
        var lowFake = new FakeAppConfigurationClient();
        lowFake.Upsert("App:RetryCount", null, "1", null);
        lowFake.Upsert("App:Label", null, "low", null);
        var highFake = new FakeAppConfigurationClient();
        highFake.Upsert("App:RetryCount", null, "2", null);
        using var lowSource = CreateSource(
            lowFake,
            new AzureAppConfigurationSourceOptions
            {
                Client = DummyClient(),
                KeyFilter = "App:*",
                TrimKeyPrefix = "App:",
            }
        );
        using var highSource = CreateSource(
            highFake,
            new AzureAppConfigurationSourceOptions
            {
                Client = DummyClient(),
                KeyFilter = "App:*",
                TrimKeyPrefix = "App:",
            }
        );
        var low = new StateSource<AppSettings.Fragment>(
            lowSource,
            new StateSourceOptions<AppSettings.Fragment> { Priority = 0 }
        );
        var high = new StateSource<AppSettings.Fragment>(
            highSource,
            new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }
        );
        var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([high, low])
        );

        var result = await runtime.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Value!.RetryCount.ShouldBe(2);
        result.Value.Label.ShouldBe("low");
    }

    [Test]
    public async Task ValueTaskCompat_WrapsResults()
    {
        ValueTask<StateReadResult<AppSettings.Fragment>> completed = ValueTaskCompat.FromResult(
            StateReadResult<AppSettings.Fragment>.NotFound("rev")
        );
        (await completed).Status.ShouldBe(StateReadStatus.NotFound);

        ValueTask<StateReadResult<AppSettings.Fragment>> failed = ValueTaskCompat.FromException<
            StateReadResult<AppSettings.Fragment>
        >(new InvalidOperationException("compat"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await failed);
    }

    private static AzureAppConfigurationSource<AppSettings.Fragment> CreateSource(
        FakeAppConfigurationClient fake,
        AzureAppConfigurationSourceOptions options
    ) => new(fake, AppSettings.FragmentSchema, options);

    private static AzureAppConfigurationSourceOptions Writable(
        Func<AzureAppConfigurationSourceOptions, AzureAppConfigurationSourceOptions> _,
        TimeSpan? refresh = null
    ) =>
        new()
        {
            Client = DummyClient(),
            KeyFilter = "App:*",
            TrimKeyPrefix = "App:",
            RefreshInterval = refresh ?? TimeSpan.FromSeconds(30),
            Writable = true,
        };

    private static ConfigurationClient DummyClient() =>
        new(new Uri("https://example.azconfig.io"), new FakeCredential());

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken
        ) => new("fake", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken
        ) => new(new AccessToken("fake", DateTimeOffset.MaxValue));
    }

    private sealed class FakeAppConfigurationClient : IAppConfigurationClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Key, string Label), StoredEntry> _store = new();
        private readonly Dictionary<
            string,
            Dictionary<(string Key, string Label), StoredEntry>
        > _snapshots = new();
        private long _etagCounter;
        private int _getSettingsCalls;

        public Func<Exception?>? ReadFault { get; init; }

        public HashSet<string> FailKeys { get; } = new(StringComparer.Ordinal);

        public int GetSettingsCallCount => Volatile.Read(ref _getSettingsCalls);

        public void Upsert(string key, string? label, string? value, string? contentType)
        {
            lock (_gate)
            {
                var entry = new StoredEntry(
                    value,
                    contentType,
                    $"\"etag-{Interlocked.Increment(ref _etagCounter)}\"",
                    DateTimeOffset.UtcNow
                );
                _store[(key, label ?? string.Empty)] = entry;
            }
        }

        public void CreateSnapshot(string name)
        {
            lock (_gate)
            {
                _snapshots[name] = new Dictionary<(string Key, string Label), StoredEntry>(_store);
            }
        }

        public Task<IReadOnlyList<AppConfigurationEntry>> GetSettingsAsync(
            AppConfigurationSelection selection,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _getSettingsCalls);
            if (ReadFault?.Invoke() is { } fault)
            {
                return Task.FromException<IReadOnlyList<AppConfigurationEntry>>(fault);
            }

            lock (_gate)
            {
                IReadOnlyDictionary<(string Key, string Label), StoredEntry> scope = _store;
                if (selection.SnapshotName is { } snapshotName)
                {
                    scope = _snapshots.TryGetValue(snapshotName, out var snapshot)
                        ? snapshot
                        : new Dictionary<(string Key, string Label), StoredEntry>();
                }

                var entries = scope
                    .Where(pair =>
                        AzureAppConfigurationKeyMapper.MatchesKeyFilter(
                            pair.Key.Key,
                            selection.KeyFilter
                        )
                        && string.Equals(
                            pair.Key.Label,
                            selection.LabelFilter ?? string.Empty,
                            StringComparison.Ordinal
                        )
                    )
                    .Select(pair => new AppConfigurationEntry(
                        pair.Key.Key,
                        pair.Key.Label.Length == 0 ? null : pair.Key.Label,
                        pair.Value.Value,
                        pair.Value.ContentType,
                        pair.Value.ETag,
                        pair.Value.LastModified
                    ))
                    .ToArray();
                return Task.FromResult<IReadOnlyList<AppConfigurationEntry>>(entries);
            }
        }

        public Task<AppConfigurationEntry?> GetSettingAsync(
            string key,
            string? label,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadFault?.Invoke() is { } fault)
            {
                return Task.FromException<AppConfigurationEntry?>(fault);
            }

            lock (_gate)
            {
                return Task.FromResult(
                    _store.TryGetValue((key, label ?? string.Empty), out var stored)
                        ? new AppConfigurationEntry(
                            key,
                            label,
                            stored.Value,
                            stored.ContentType,
                            stored.ETag,
                            stored.LastModified
                        )
                        : null
                );
            }
        }

        public Task<AppConfigurationEntry> SetSettingAsync(
            string key,
            string value,
            string? label,
            string? contentType,
            AppConfigurationWriteCondition condition,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (FailKeys.Contains(key))
                {
                    throw new StateConflictException(
                        $"The setting '{key}' was rejected by the fake."
                    );
                }

                var id = (key, label ?? string.Empty);
                if (condition.IsMustNotExist && _store.ContainsKey(id))
                {
                    throw new StateConflictException($"The setting '{key}' already exists.");
                }

                if (condition.IsMatch)
                {
                    if (
                        !_store.TryGetValue(id, out var current)
                        || !string.Equals(current.ETag, condition.ETag, StringComparison.Ordinal)
                    )
                    {
                        throw new StateConflictException(
                            $"The setting '{key}' changed after it was read."
                        );
                    }
                }

                var stored = new StoredEntry(
                    value,
                    contentType,
                    $"\"etag-{Interlocked.Increment(ref _etagCounter)}\"",
                    DateTimeOffset.UtcNow
                );
                _store[id] = stored;
                return Task.FromResult(
                    new AppConfigurationEntry(
                        key,
                        label,
                        stored.Value,
                        stored.ContentType,
                        stored.ETag,
                        stored.LastModified
                    )
                );
            }
        }

        private sealed record StoredEntry(
            string? Value,
            string? ContentType,
            string? ETag,
            DateTimeOffset? LastModified
        );
    }
}
