using Configlue.Sources;

namespace Configlue.Tests;

public sealed class KeyedSecretSourceTests
{
    private static KeyedSecretSource<AppSettings.Fragment> CreateSource(
        FakeKeyedClient client,
        IReadOnlyList<KeyedSecretMapping>? mappings = null,
        bool writable = false,
        TimeSpan? pollInterval = null,
        bool convention = false,
        string? conventionPrefix = null,
        string? defaultVersion = null,
        Func<string, Type, object?>? parser = null
    ) =>
        new(
            client,
            AppSettings.ConfiglueSchema,
            mappings ?? [new KeyedSecretMapping("Label", "app-label")],
            new KeyedSecretSourceOptions
            {
                EnableConventionMapping = convention,
                ConventionPrefix = conventionPrefix,
                ConventionSeparator = "-",
                Writable = writable,
                PollInterval = pollInterval,
                DefaultVersion = defaultVersion,
                ValueParser = parser,
                PhysicalOrigin = "test:keyed",
            }
        );

    [Test]
    public async Task ExplicitMapping_ReadsTypedMembers()
    {
        var client = new FakeKeyedClient();
        client.Set("app-label", "hello");
        client.Set("retry-count", "7");
        client.Set("db-host", "db.example");
        client.Set("plugins", """["a","b"]""");
        var source = CreateSource(
            client,
            [
                new KeyedSecretMapping("Label", "app-label"),
                new KeyedSecretMapping("RetryCount", "retry-count"),
                new KeyedSecretMapping("Database.Host", "db-host"),
                new KeyedSecretMapping("Plugins", "plugins"),
            ]
        );

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Label.Value.ShouldBe("hello");
        read.Value.RetryCount.Value.ShouldBe(7);
        read.Value.Database.Value!.Host.Value.ShouldBe("db.example");
        read.Value.Plugins.Value.ShouldBe(["a", "b"]);
        read.Revision.ShouldNotBeNullOrWhiteSpace();
        read.Revision!.Length.ShouldBe(64);
    }

    [Test]
    public async Task ConventionMapping_DerivesDeterministicNames()
    {
        var client = new FakeKeyedClient();
        client.Set("Label", "convention-label");
        client.Set("Database-Host", "convention-db");
        var source = CreateSource(client, [], convention: true);

        var resolved = source.ResolvedMappings.Select(static m => m.Key).ToArray();
        resolved.ShouldContain("Label");
        resolved.ShouldContain("Database-Host");

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Label.Value.ShouldBe("convention-label");
    }

    [Test]
    public async Task ConventionMapping_UsesPrefixAndExplicitWins()
    {
        var client = new FakeKeyedClient();
        client.Set("app-Label", "prefixed");
        client.Set("custom-label", "explicit");
        var source = CreateSource(
            client,
            [new KeyedSecretMapping("Label", "custom-label")],
            convention: true,
            conventionPrefix: "app"
        );

        var label = source.ResolvedMappings.Single(m =>
            string.Equals(m.PropertyPath, "Label", StringComparison.OrdinalIgnoreCase)
        );
        label.Key.ShouldBe("custom-label");

        var read = await source.ReadAsync();
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Label.Value.ShouldBe("explicit");
    }

    [Test]
    public void MappingValidation_RejectsBadMappings()
    {
        var client = new FakeKeyedClient();

        Should.Throw<ArgumentException>(() =>
            CreateSource(client, [new KeyedSecretMapping("Missing.Member", "ok-key")])
        );
        Should.Throw<ArgumentException>(() =>
            CreateSource(client, [new KeyedSecretMapping("Database", "ok-key")])
        );
        Should.Throw<ArgumentException>(() =>
            CreateSource(
                client,
                [
                    new KeyedSecretMapping("Label", "dup-key"),
                    new KeyedSecretMapping("RetryCount", "dup-key"),
                ]
            )
        );
        Should.Throw<ArgumentException>(() =>
            CreateSource(
                client,
                [
                    new KeyedSecretMapping("Label", "k1"),
                    new KeyedSecretMapping("label", "k2"),
                ]
            )
        );
        Should.Throw<ArgumentException>(() => CreateSource(client, []));
        Should.Throw<ArgumentException>(() =>
            CreateSource(client, [new KeyedSecretMapping("Label.Trailing.", "k1")])
        );
    }

    [Test]
    public async Task ScalarParsing_CoversCommonLeafTypes()
    {
        var client = new FakeKeyedClient();
        client.Set("k-enabled", "false");
        client.Set("k-retry", "9");
        client.Set("k-label", "text");
        client.Set("k-host", "h.example");
        var source = CreateSource(
            client,
            [
                new KeyedSecretMapping("Enabled", "k-enabled"),
                new KeyedSecretMapping("RetryCount", "k-retry"),
                new KeyedSecretMapping("Label", "k-label"),
                new KeyedSecretMapping("Database.Host", "k-host"),
            ]
        );

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Enabled.Value.ShouldBeFalse();
        read.Value.RetryCount.Value.ShouldBe(9);
        read.Value.Label.Value.ShouldBe("text");
        read.Value.Database.Value!.Host.Value.ShouldBe("h.example");
    }

    [Test]
    public async Task CustomParser_OverridesScalarWithJsonFallback()
    {
        var client = new FakeKeyedClient();
        client.Set("k-retry", "42");
        client.Set("k-plugins", """["x"]""");
        var source = CreateSource(
            client,
            [
                new KeyedSecretMapping("RetryCount", "k-retry"),
                new KeyedSecretMapping("Plugins", "k-plugins"),
            ],
            parser: static (text, type) =>
                type == typeof(int) ? int.Parse(text) * 2 : throw new NotSupportedException()
        );

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.RetryCount.Value.ShouldBe(84);
        read.Value.Plugins.Value.ShouldBe(["x"]);
    }

    [Test]
    public async Task MalformedValues_MapToInvalidPayloadWithoutLeaking()
    {
        const string secretValue = "not-an-int-SECRET-1";
        var client = new FakeKeyedClient();
        client.Set("k-retry", secretValue);
        var source = CreateSource(client, [new KeyedSecretMapping("RetryCount", "k-retry")]);

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.InvalidPayload);
        read.Revision!.ShouldNotContain(secretValue);
    }

    [Test]
    public async Task Throttling_MapsToUnavailable()
    {
        var client = new FakeKeyedClient { ThrowUnavailable = true };
        var source = CreateSource(client);

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Unavailable);
    }

    [Test]
    public async Task MissingKeys_ProduceSparseNotFound()
    {
        var client = new FakeKeyedClient();
        client.Set("other", "unrelated", enabled: true);
        var source = CreateSource(client);

        var empty = await source.ReadAsync();
        empty.Status.ShouldBe(StateReadStatus.NotFound);

        client.Set("app-label", "present");
        var partial = await source.ReadAsync();
        partial.Status.ShouldBe(StateReadStatus.Success);
    }

    [Test]
    public async Task DisabledKeys_AreTreatedAsMissing()
    {
        var client = new FakeKeyedClient();
        client.Set("app-label", "value", enabled: false);
        var source = CreateSource(client);

        var read = await source.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task Revision_IsStableAcrossKeyOrderAndChangesOnVersion()
    {
        var firstClient = new FakeKeyedClient();
        firstClient.Set("k-b", "b", version: "v1");
        firstClient.Set("k-a", "a", version: "v2");
        var first = CreateSource(
            firstClient,
            [new KeyedSecretMapping("Label", "k-a"), new KeyedSecretMapping("RetryCount", "k-b")]
        );

        var secondClient = new FakeKeyedClient();
        secondClient.Set("k-a", "a", version: "v2");
        secondClient.Set("k-b", "b", version: "v1");
        var second = CreateSource(
            secondClient,
            [new KeyedSecretMapping("Label", "k-a"), new KeyedSecretMapping("RetryCount", "k-b")]
        );

        var firstRead = await first.ReadAsync();
        var secondRead = await second.ReadAsync();
        firstRead.Revision.ShouldBe(secondRead.Revision);

        secondClient.Set("k-a", "changed", version: "v3");
        var thirdRead = await second.ReadAsync();
        thirdRead.Revision.ShouldNotBe(firstRead.Revision);
    }

    [Test]
    public async Task FixedVersion_HasNoWatcherWhileCurrentVersionPolls()
    {
        var client = new FakeKeyedClient();
        client.Set("k", "v1", version: "aaa");
        var fixedSource = CreateSource(
            client,
            [new KeyedSecretMapping("Label", "k", "aaa")],
            pollInterval: TimeSpan.FromMilliseconds(20)
        );

        fixedSource.IsAllFixedVersion.ShouldBeTrue();
        fixedSource.Watcher.ShouldBeNull();

        var polling = CreateSource(
            client,
            [new KeyedSecretMapping("Label", "k")],
            pollInterval: TimeSpan.FromMilliseconds(20)
        );
        polling.Watcher.ShouldNotBeNull();
        var observed = await polling.ReadAsync();
        var wait = polling.WaitForChangeAsync(ConfiglueResourceContext.Default, observed.Revision);
        await Task.Delay(50);
        client.Set("k", "v2");
        await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task WritableSource_PersistsPresentMembersAndRejectsConditions()
    {
        var client = new FakeKeyedClient();
        var source = CreateSource(
            client,
            [
                new KeyedSecretMapping("Label", "k-label"),
                new KeyedSecretMapping("RetryCount", "k-retry"),
            ],
            writable: true
        );

        var fragment = new AppSettings.Fragment
        {
            Label = Optional<string?>.Present("written"),
            RetryCount = Optional<int>.Present(42),
        };
        var receipt = await source.WriteAsync(new StateWriteRequest<AppSettings.Fragment>(fragment));
        receipt.Revision.ShouldNotBeNullOrWhiteSpace();

        var read = await source.ReadAsync();
        read.Value!.Label.Value.ShouldBe("written");
        read.Value.RetryCount.Value.ShouldBe(42);

        var conditional = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await source.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(
                    fragment,
                    Condition: RevisionCondition.Match("anything")
                )
            )
        );
        conditional.Message.ShouldNotContain("written");

        var readOnly = CreateSource(client);
        readOnly.Writer.ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await readOnly.WriteAsync(new StateWriteRequest<AppSettings.Fragment>(fragment))
        );
    }

    [Test]
    public async Task Diagnostics_NeverExposeSecretValues()
    {
        const string secretValue = "SECRET-VALUE-999";
        var client = new FakeKeyedClient();
        client.Set("k-label", secretValue);
        var source = CreateSource(client, [new KeyedSecretMapping("Label", "k-label")]);

        var read = await source.ReadAsync();
        read.Revision!.ShouldNotContain(secretValue);
        read.PhysicalOrigin!.ShouldNotContain(secretValue);
        source.ToString()!.ShouldNotContain(secretValue);
    }

    private sealed class FakeKeyedClient : IKeyedSecretClient
    {
        private readonly Dictionary<string, Stored> _secrets = new(StringComparer.Ordinal);
        private int _versionCounter;

        public bool ThrowUnavailable { get; set; }

        public void Set(string key, string value, string? version = null, bool enabled = true) =>
            _secrets[key] = new Stored(value, version ?? $"v{++_versionCounter}-{key}", enabled);

        public ValueTask<KeyedSecretValue?> GetAsync(
            string key,
            string? version,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowUnavailable)
            {
                throw new KeyedSecretUnavailableException($"Key '{key}' unavailable.");
            }

            if (!_secrets.TryGetValue(key, out var stored))
            {
                return new ValueTask<KeyedSecretValue?>((KeyedSecretValue?)null);
            }

            if (version is not null && !string.Equals(stored.Version, version, StringComparison.Ordinal))
            {
                return new ValueTask<KeyedSecretValue?>((KeyedSecretValue?)null);
            }

            return new ValueTask<KeyedSecretValue?>(
                new KeyedSecretValue(stored.Value, stored.Version, stored.Enabled)
            );
        }

        public ValueTask<string?> SetAsync(
            string key,
            string value,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowUnavailable)
            {
                throw new KeyedSecretUnavailableException($"Key '{key}' unavailable.");
            }

            var version = $"v{++_versionCounter}-{key}-set";
            _secrets[key] = new Stored(value, version, true);
            return new ValueTask<string?>(version);
        }

        private sealed record Stored(string Value, string Version, bool Enabled);
    }
}
