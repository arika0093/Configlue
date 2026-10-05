using System.Collections.Concurrent;
using System.Text;
using Configlue.Provider.Json;
using Configlue.Resource.Vault;

namespace Configlue.Tests;

public sealed class VaultKvResourceTests
{
    [Test]
    public async Task ReadAsync_ReturnsContentAndVersion()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", """{"RetryCount": 7}""", 5);
        using var resource = new VaultKvResource(client, "secret", "config/app");

        var result = await resource.ReadAsync(ConfiglueResourceContext.Default);

        result.Status.ShouldBe(StateReadStatus.Success);
        Encoding.UTF8.GetString(result.Content.ToArray()).ShouldBe("""{"RetryCount": 7}""");
        result.Revision.ShouldBe("5");
        client.LastMount.ShouldBe("secret");
        client.LastPath.ShouldBe("config/app");
    }

    [Test]
    public async Task ReadAsync_MapsMissingSecretToNotFound()
    {
        var client = new FakeVaultKvClient();
        using var resource = new VaultKvResource(client, "secret", "missing");

        var result = await resource.ReadAsync(ConfiglueResourceContext.Default);

        result.Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task ReadAsync_MapsDeletedSecretToNotFound()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", """{"RetryCount": 1}""", 2);
        using var resource = new VaultKvResource(client, "secret", "config/app");

        (await resource.ReadAsync(ConfiglueResourceContext.Default)).Status.ShouldBe(
            StateReadStatus.Success
        );
        client.Delete("secret", "config/app");

        (await resource.ReadAsync(ConfiglueResourceContext.Default)).Status.ShouldBe(
            StateReadStatus.NotFound
        );
    }

    [Test]
    public async Task ReadAsync_V1ReportsStableContentHashRevision()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", """{"RetryCount": 1}""", null);
        using var resource = new VaultKvResource(
            client,
            "secret",
            "config/app",
            new VaultKvResourceOptions { KvVersion = VaultKvEngine.V1 }
        );

        var first = await resource.ReadAsync(ConfiglueResourceContext.Default);
        var second = await resource.ReadAsync(ConfiglueResourceContext.Default);

        first.Status.ShouldBe(StateReadStatus.Success);
        first.Revision.ShouldNotBeNullOrWhiteSpace();
        first.Revision.ShouldBe(second.Revision);
    }

    [Test]
    public async Task ReadAsync_RejectsInvalidVersions()
    {
        foreach (var version in new long?[] { null, 0, -3 })
        {
            var client = new FakeVaultKvClient();
            client.SeedRaw("secret", "config/app", [1, 2, 3], version);
            using var resource = new VaultKvResource(client, "secret", "config/app");

            await Should.ThrowAsync<InvalidDataException>(async () =>
                await resource.ReadAsync(ConfiglueResourceContext.Default)
            );
        }
    }

    [Test]
    public async Task ReadAsync_SurfacesTransientFailures()
    {
        var client = new FakeVaultKvClient { ReadFailure = new VaultKvTransientException("busy") };
        using var resource = new VaultKvResource(client, "secret", "config/app");

        var failure = await Should.ThrowAsync<VaultKvTransientException>(async () =>
            await resource.ReadAsync(ConfiglueResourceContext.Default)
        );
        failure.Message.ShouldBe("busy");
    }

    [Test]
    public async Task ReadAsync_RespectsCancellation()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", "payload", 1);
        using var resource = new VaultKvResource(client, "secret", "config/app");

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.ReadAsync(
                ConfiglueResourceContext.Default,
                new CancellationToken(canceled: true)
            )
        );
        client.SecretReads.ShouldBe(0);
    }

    [Test]
    public async Task SubjectAwareAddressSelectsMountPathAndClient()
    {
        var defaultClient = new FakeVaultKvClient();
        var routedClient = new FakeVaultKvClient();
        routedClient.Seed("tenant-secret", "tenant-a/app", "routed", 4);
        using var resource = new VaultKvResource(
            defaultClient,
            "secret",
            "config/app",
            new VaultKvResourceOptions
            {
                MountSelector = context => $"tenant-{context.Route.Value}",
                PathSelector = context => $"{((TenantSubject)context.Subject).Tenant}/app",
                ClientSelector = context =>
                    context.Route.Value == "secret" ? routedClient : defaultClient,
            }
        );
        var context = new ConfiglueResourceContext(
            new TenantSubject("tenant-a"),
            ResourceKey.From(SubjectKey.From("tenant-a")),
            RouteKey.From("secret")
        );

        var result = await resource.ReadAsync(context);

        Encoding.UTF8.GetString(result.Content.ToArray()).ShouldBe("routed");
        result.Revision.ShouldBe("4");
        routedClient.LastMount.ShouldBe("tenant-secret");
        routedClient.LastPath.ShouldBe("tenant-a/app");
        defaultClient.SecretReads.ShouldBe(0);
        using var plain = new VaultKvResource(new FakeVaultKvClient(), "secret", "config/app");
        resource
            .GetResourceId(context)
            .ShouldNotBe(plain.GetResourceId(ConfiglueResourceContext.Default));
    }

    [Test]
    public async Task WriteAsync_UsesCheckAndSetAndReturnsNewVersion()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", "old", 5);
        using var resource = new VaultKvResource(client, "secret", "config/app");

        var result = await resource.WriteAsync(
            ConfiglueResourceContext.Default,
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes("new"),
                Condition: RevisionCondition.Match("5")
            )
        );

        result.Revision.ShouldBe("6");
        client.LastExpectedVersion.ShouldBe(5);
        client.LastRequireMissing.ShouldBeFalse();
        Encoding.UTF8.GetString(client.LastContent.ToArray()).ShouldBe("new");
    }

    [Test]
    public async Task WriteAsync_MapsStaleVersionToConflictWithoutLeakingSecrets()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", "super-secret-value", 6);
        using var resource = new VaultKvResource(client, "secret", "config/app");

        var failure = await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest(new byte[] { 9 }, Condition: RevisionCondition.Match("5"))
            )
        );
        failure.Message.ShouldContain("secret/config/app");
        failure.Message.ShouldNotContain("super-secret-value");
    }

    [Test]
    public async Task WriteAsync_DoesNotPropagateLeakyTransportMessages()
    {
        var client = new FakeVaultKvClient
        {
            WriteFailure = new VaultKvConflictException("leak: super-secret-value"),
        };
        using var resource = new VaultKvResource(client, "secret", "config/app");

        var failure = await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest(new byte[] { 1 }, Condition: RevisionCondition.Match("2"))
            )
        );
        failure.Message.ShouldNotContain("super-secret-value");
        failure.Message.ShouldContain("secret/config/app");
    }

    [Test]
    public async Task WriteAsync_RequiresMissingObjectWhenRevisionCheckHasNoRevision()
    {
        var client = new FakeVaultKvClient();
        using var resource = new VaultKvResource(client, "secret", "config/app");

        var result = await resource.WriteAsync(
            ConfiglueResourceContext.Default,
            new ResourceWriteRequest(new byte[] { 1 }, Condition: RevisionCondition.MustNotExist)
        );

        result.Revision.ShouldBe("1");
        client.LastExpectedVersion.ShouldBeNull();
        client.LastRequireMissing.ShouldBeTrue();
    }

    [Test]
    public async Task WriteAsync_IsUnconditionalUnlessRevisionCheckIsRequested()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", "old", 3);
        using var resource = new VaultKvResource(client, "secret", "config/app");

        var result = await resource.WriteAsync(
            ConfiglueResourceContext.Default,
            new ResourceWriteRequest(new byte[] { 9 })
        );

        result.Revision.ShouldBe("4");
        client.LastExpectedVersion.ShouldBeNull();
        client.LastRequireMissing.ShouldBeFalse();
    }

    [Test]
    public async Task WriteAsync_RejectsUnparseableRevisionsAsConflict()
    {
        var client = new FakeVaultKvClient();
        using var resource = new VaultKvResource(client, "secret", "config/app");

        foreach (var revision in new[] { "not-a-version", "0", "-1", "" })
        {
            await Should.ThrowAsync<StateConflictException>(async () =>
                await resource.WriteAsync(
                    ConfiglueResourceContext.Default,
                    new ResourceWriteRequest(
                        new byte[] { 1 },
                        Condition: RevisionCondition.Match(revision)
                    )
                )
            );
        }

        client.WriteCalls.ShouldBe(0);
    }

    [Test]
    public async Task WriteAsync_V1MatchComparesAgainstAFreshRead()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", "v1-payload", null);
        using var resource = new VaultKvResource(
            client,
            "secret",
            "config/app",
            new VaultKvResourceOptions { KvVersion = VaultKvEngine.V1 }
        );
        var observed = await resource.ReadAsync(ConfiglueResourceContext.Default);

        var result = await resource.WriteAsync(
            ConfiglueResourceContext.Default,
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes("v1-next"),
                Condition: RevisionCondition.Match(observed.Revision!)
            )
        );

        result.Revision.ShouldBe(
            (await resource.ReadAsync(ConfiglueResourceContext.Default)).Revision
        );
        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest(
                    new byte[] { 7 },
                    Condition: RevisionCondition.Match(observed.Revision!)
                )
            )
        );
    }

    [Test]
    public async Task WaitForChangeAsync_ReturnsImmediatelyWhenAlreadyChanged()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", "payload", 3);
        using var resource = new VaultKvResource(client, "secret", "config/app");

        await resource
            .WaitForChangeAsync(ConfiglueResourceContext.Default, "2")
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(30));

        // Metadata polling avoids re-downloading payloads when nothing changed.
        client.SecretReads.ShouldBe(0);
        client.MetadataReads.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task WaitForChangeAsync_ReturnsImmediatelyWhenSecretIsDeleted()
    {
        var client = new FakeVaultKvClient();
        using var resource = new VaultKvResource(client, "secret", "config/app");

        await resource
            .WaitForChangeAsync(ConfiglueResourceContext.Default, "9")
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task WaitForChangeAsync_WakesWhenVersionAdvances()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", "payload", 1);
        using var resource = new VaultKvResource(
            client,
            "secret",
            "config/app",
            new VaultKvResourceOptions { PollingInterval = TimeSpan.FromSeconds(1) }
        );

        var wait = resource.WaitForChangeAsync(ConfiglueResourceContext.Default, "1").AsTask();
        await Task.Delay(100);
        await resource.WriteAsync(
            ConfiglueResourceContext.Default,
            new ResourceWriteRequest(new byte[] { 2 })
        );
        await wait.WaitAsync(TimeSpan.FromSeconds(30));

        client.SecretReads.ShouldBe(0);
    }

    [Test]
    public async Task WaitForChangeAsync_RetriesTransientFailuresThenWakes()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", "payload", 1);
        client.MetadataFailures.Enqueue(new VaultKvTransientException("flake-1"));
        client.MetadataFailures.Enqueue(new VaultKvTransientException("flake-2"));
        using var resource = new VaultKvResource(
            client,
            "secret",
            "config/app",
            new VaultKvResourceOptions { PollingInterval = TimeSpan.FromSeconds(1) }
        );

        var wait = resource.WaitForChangeAsync(ConfiglueResourceContext.Default, "1").AsTask();
        await Task.Delay(100);
        await resource.WriteAsync(
            ConfiglueResourceContext.Default,
            new ResourceWriteRequest(new byte[] { 2 })
        );
        await wait.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task WaitForChangeAsync_V1WatchesContent()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", "before", null);
        using var resource = new VaultKvResource(
            client,
            "secret",
            "config/app",
            new VaultKvResourceOptions
            {
                KvVersion = VaultKvEngine.V1,
                PollingInterval = TimeSpan.FromSeconds(1),
            }
        );
        var observed = await resource.ReadAsync(ConfiglueResourceContext.Default);

        var wait = resource
            .WaitForChangeAsync(ConfiglueResourceContext.Default, observed.Revision)
            .AsTask();
        await Task.Delay(100);
        await resource.WriteAsync(
            ConfiglueResourceContext.Default,
            new ResourceWriteRequest(Encoding.UTF8.GetBytes("after"))
        );
        await wait.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task WaitForChangeAsync_HonorsCancellation()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", "payload", 1);
        using var resource = new VaultKvResource(
            client,
            "secret",
            "config/app",
            new VaultKvResourceOptions { PollingInterval = TimeSpan.FromSeconds(1) }
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(200);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource
                .WaitForChangeAsync(ConfiglueResourceContext.Default, "1", cancellation.Token)
                .AsTask()
        );
    }

    [Test]
    public async Task DisposedResourceStopsWatchersAndRejectsOperations()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "config/app", "payload", 1);
        var resource = new VaultKvResource(
            client,
            "secret",
            "config/app",
            new VaultKvResourceOptions { PollingInterval = TimeSpan.FromSeconds(1) }
        );

        var wait = resource.WaitForChangeAsync(ConfiglueResourceContext.Default, "1").AsTask();
        await Task.Delay(100);
        resource.Dispose();
        resource.Dispose();

        // Disposal shares the common polling-watch semantics: active waiters wake
        // successfully instead of hanging (see PollingWatchTests).
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await resource.ReadAsync(ConfiglueResourceContext.Default)
        );
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await resource.WriteAsync(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest(new byte[] { 1 })
            )
        );
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await resource.WaitForChangeAsync(ConfiglueResourceContext.Default, "1").AsTask()
        );
    }

    [Test]
    public void ResourceId_IsStableForMountAndPathAndCanBeOverridden()
    {
        using var first = new VaultKvResource(new FakeVaultKvClient(), "secret", "config/app");
        using var same = new VaultKvResource(new FakeVaultKvClient(), "secret", "config/app");
        using var different = new VaultKvResource(
            new FakeVaultKvClient(),
            "secret",
            "config/other"
        );
        var overridden = new ResourceId("deployment:settings");
        using var withOverride = new VaultKvResource(
            new FakeVaultKvClient(),
            "secret",
            "config/app",
            new VaultKvResourceOptions { FixedResourceId = overridden }
        );

        var context = ConfiglueResourceContext.Default;
        var id = first.GetResourceId(context);
        id.Value.ShouldStartWith("vault:");
        same.GetResourceId(context).ShouldBe(id);
        different.GetResourceId(context).ShouldNotBe(id);
        withOverride.GetResourceId(context).ShouldBe(overridden);
    }

    [Test]
    public void VaultDoesNotProvideMultiPathTransactions()
    {
        using var resource = new VaultKvResource(new FakeVaultKvClient(), "secret", "config/app");

        ((object)resource is IResourceBatchWriter).ShouldBeFalse();
        using var other = new VaultKvResource(new FakeVaultKvClient(), "secret", "config/other");
        resource
            .GetResourceId(ConfiglueResourceContext.Default)
            .ShouldNotBe(other.GetResourceId(ConfiglueResourceContext.Default));
    }

    [Test]
    public void NamingPolicy_RejectsAmbiguousAddressing()
    {
        var client = new FakeVaultKvClient();
        foreach (var mount in new[] { "", "  ", "/secret", "a/b", "a\\b", "a/../b", "a//b" })
        {
            Should.Throw<ArgumentException>(() => new VaultKvResource(client, mount, "config/app"));
        }

        foreach (
            var path in new[]
            {
                "",
                "  ",
                "/config/app",
                "config\\app",
                "config/../other",
                "config//app",
                "config/./app",
            }
        )
        {
            Should.Throw<ArgumentException>(() => new VaultKvResource(client, "secret", path));
        }

        Should.Throw<ArgumentException>(() => VaultKvPath.ValidateMount("has\0nul"));
    }

    [Test]
    public void Options_RejectInvalidPollingIntervalsAndEngines()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new VaultKvResource(
                new FakeVaultKvClient(),
                "secret",
                "config/app",
                new VaultKvResourceOptions { PollingInterval = TimeSpan.FromMilliseconds(50) }
            )
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new VaultKvResource(
                new FakeVaultKvClient(),
                "secret",
                "config/app",
                new VaultKvResourceOptions { KvVersion = (VaultKvEngine)99 }
            )
        );
        Should.Throw<ArgumentNullException>(() =>
            new VaultKvResource(null!, "secret", "config/app")
        );
    }

    [Test]
    public void ToString_RedactsSecretsAndTokens()
    {
        using var resource = new VaultKvResource(new FakeVaultKvClient(), "secret", "config/app");

        var description = resource.ToString();
        description.ShouldContain("secret/config/app");
        description.ShouldNotContain("super-secret-value");
        description.ShouldNotContain("s3cr3t-t0k3n-vault");
        resource
            .GetResourceId(ConfiglueResourceContext.Default)
            .Value.ShouldNotContain("super-secret-value");
    }

    [Test]
    public async Task VaultSourcesWithDifferentPathsGetDistinctLogicalIds()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "one", """{"RetryCount": 1}""", 1);
        client.Seed("secret", "two", """{"RetryCount": 2}""", 1);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromVaultKv(CreateSourceOptions("one", client));
                    sources.FromVaultKv(CreateSourceOptions("two", client));
                })
            );
        });

        var sources = context.GetRuntimeState<AppSettings>().GetDiagnostics().Sources;
        sources.Count.ShouldBe(2);
        sources.Select(static source => source.Id).Distinct().Count().ShouldBe(2);
        sources.All(static source => source.FixedResourceId is null).ShouldBeTrue();
        sources
            .Select(static source => source.PhysicalOrigin)
            .ShouldBe(["vault:secret/one", "vault:secret/two"]);
        sources.All(static source => source.CanRead).ShouldBeTrue();
        foreach (var source in sources)
        {
            (source.PhysicalOrigin ?? string.Empty).ShouldNotContain("super-secret-value");
        }
    }

    [Test]
    public async Task WritableVaultSourceAdvertisesWriteAndWatchCapabilities()
    {
        var client = new FakeVaultKvClient();

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromVaultKv(
                        new VaultKvSourceOptions
                        {
                            Mount = "secret",
                            Path = "config/app",
                            Client = client,
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                        }
                    );
                })
            );
        });

        var sources = context.GetRuntimeState<AppSettings>().GetDiagnostics().Sources;
        sources.Count.ShouldBe(1);
        sources[0].CanRead.ShouldBeTrue();
        sources[0].CanWrite.ShouldBeTrue();
        sources[0].CanWatch.ShouldBeTrue();
        sources[0].PhysicalOrigin.ShouldBe("vault:secret/config/app");
    }

    [Test]
    public async Task HigherPriorityVaultSourceWinsAndMissingFallsThrough()
    {
        var client = new FakeVaultKvClient();
        client.Seed("secret", "overlay", """{"RetryCount": 9}""", 2);
        client.Seed("secret", "base", """{"RetryCount": 3}""", 1);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromVaultKv(CreateSourceOptions("overlay", client, priority: 10));
                    sources.FromVaultKv(CreateSourceOptions("base", client));
                })
            );
        });

        (await context.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(9);

        client.Delete("secret", "overlay");

        (await context.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(3);
    }

    [Test]
    public async Task NestedPayloadRoundTripsThroughTheCodecPipeline()
    {
        var client = new FakeVaultKvClient();
        client.Seed(
            "secret",
            "config/app",
            """{"RetryCount": 7, "Database": {"Host": "vault-host", "Port": 5433}}""",
            1
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromVaultKv(CreateSourceOptions("config/app", client));
                })
            );
        });

        var value = await context.GetState<AppSettings>().GetValueAsync();
        value.RetryCount.ShouldBe(7);
        value.Database!.Host.ShouldBe("vault-host");
        value.Database.Port.ShouldBe(5433);
    }

    [Test]
    public async Task ReadOnlyVaultSourceExposesNoWriterOrWatcher()
    {
        var client = new FakeVaultKvClient();

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromVaultKv(
                        new VaultKvSourceOptions
                        {
                            Mount = "secret",
                            Path = "config/app",
                            Client = client,
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                            Writable = false,
                            EnableWatch = false,
                        }
                    );
                })
            );
        });

        var sources = context.GetRuntimeState<AppSettings>().GetDiagnostics().Sources;
        sources.Count.ShouldBe(1);
        sources[0].CanWrite.ShouldBeFalse();
        sources[0].CanWatch.ShouldBeFalse();
        sources[0].CanRead.ShouldBeTrue();
    }

    [Test]
    public void FromVaultKv_ValidatesOptions()
    {
        var client = new FakeVaultKvClient();
        var codec = StateCodecBinding.Typed(new JsonStateCodec<AppSettings.Fragment>());
        var builder = new ConfiglueSourceSetBuilder();

        Should.Throw<ArgumentException>(() =>
            builder.FromVaultKv(
                new VaultKvSourceOptions
                {
                    Mount = "secret",
                    Path = "config/app",
                    Client = client,
                    ClientFactory = _ => client,
                    Codec = codec,
                }
            )
        );
        Should.Throw<ArgumentException>(() =>
            builder.FromVaultKv(
                new VaultKvSourceOptions
                {
                    Mount = "secret",
                    Path = "config/app",
                    Codec = codec,
                }
            )
        );
        Should.Throw<ArgumentException>(() =>
            builder.FromVaultKv(
                new VaultKvSourceOptions
                {
                    Mount = "/secret",
                    Path = "config/app",
                    Client = client,
                    Codec = codec,
                }
            )
        );
    }

    [Test]
    public async Task FromVaultKv_SurfacesNullClientFactories()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await using var context = ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        sources.FromVaultKv(
                            new VaultKvSourceOptions
                            {
                                Mount = "secret",
                                Path = "config/app",
                                ClientFactory = _ => null!,
                                Codec = StateCodecBinding.Typed(
                                    new JsonStateCodec<AppSettings.Fragment>()
                                ),
                            }
                        );
                    })
                );
            });
            _ = await context.GetState<AppSettings>().GetValueAsync();
        });
    }

    private static VaultKvSourceOptions CreateSourceOptions(
        string path,
        FakeVaultKvClient client,
        int priority = 0
    ) =>
        new()
        {
            Mount = "secret",
            Path = path,
            Client = client,
            Codec = StateCodecBinding.Typed(new JsonStateCodec<AppSettings.Fragment>()),
            Writable = false,
            EnableWatch = false,
            Priority = priority,
        };

    private sealed record TenantSubject(string Tenant) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Tenant);
    }

    private sealed class FakeVaultKvClient : IVaultKvClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Mount, string Path), StoredSecret> _secrets = [];

        public ConcurrentQueue<Exception> MetadataFailures { get; } = new();

        public Exception? ReadFailure { get; set; }

        public Exception? WriteFailure { get; set; }

        public string? LastMount { get; private set; }

        public string? LastPath { get; private set; }

        public long? LastExpectedVersion { get; private set; }

        public bool LastRequireMissing { get; private set; }

        public ReadOnlyMemory<byte> LastContent { get; private set; }

        private int _secretReads;

        private int _metadataReads;

        private int _writeCalls;

        public int SecretReads => Volatile.Read(ref _secretReads);

        public int MetadataReads => Volatile.Read(ref _metadataReads);

        public int WriteCalls => Volatile.Read(ref _writeCalls);

        public void Seed(string mount, string path, string content, long? version) =>
            SeedRaw(mount, path, Encoding.UTF8.GetBytes(content), version);

        public void SeedRaw(string mount, string path, byte[] content, long? version)
        {
            lock (_gate)
            {
                _secrets[(mount, path)] = new StoredSecret(content, version);
            }
        }

        public void Delete(string mount, string path)
        {
            lock (_gate)
            {
                _secrets.Remove((mount, path));
            }
        }

        public Task<VaultKvSecret?> ReadSecretAsync(
            string mount,
            string path,
            long? version,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _secretReads);
            LastMount = mount;
            LastPath = path;
            lock (_gate)
            {
                if (ReadFailure is { } failure)
                {
                    throw failure;
                }

                return Task.FromResult(
                    _secrets.TryGetValue((mount, path), out var stored)
                        ? new VaultKvSecret(stored.Content, stored.Version)
                        : null
                );
            }
        }

        public Task<VaultKvSecretMetadata?> ReadMetadataAsync(
            string mount,
            string path,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _metadataReads);
            lock (_gate)
            {
                if (MetadataFailures.TryDequeue(out var failure))
                {
                    throw failure;
                }

                return Task.FromResult(
                    _secrets.TryGetValue((mount, path), out var stored)
                        ? new VaultKvSecretMetadata(stored.Version)
                        : null
                );
            }
        }

        public Task<long?> WriteSecretAsync(
            string mount,
            string path,
            ReadOnlyMemory<byte> content,
            long? expectedVersion,
            bool requireMissing,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteFailure is { } failure)
            {
                return Task.FromException<long?>(failure);
            }

            LastMount = mount;
            LastPath = path;
            LastExpectedVersion = expectedVersion;
            LastRequireMissing = requireMissing;
            LastContent = content;
            Interlocked.Increment(ref _writeCalls);
            lock (_gate)
            {
                _secrets.TryGetValue((mount, path), out var current);
                if (requireMissing && current is not null)
                {
                    throw new VaultKvConflictException(
                        $"The secret '{mount}/{path}' already exists."
                    );
                }

                if (expectedVersion.HasValue && current?.Version != expectedVersion)
                {
                    throw new VaultKvConflictException(
                        $"The secret '{mount}/{path}' changed after version {expectedVersion}."
                    );
                }

                var next = (current?.Version ?? 0) + 1;
                _secrets[(mount, path)] = new StoredSecret(content.ToArray(), next);
                return Task.FromResult<long?>(next);
            }
        }

        private sealed record StoredSecret(byte[] Content, long? Version);
    }
}
