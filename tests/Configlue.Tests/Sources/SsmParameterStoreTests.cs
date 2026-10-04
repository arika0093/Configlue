using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Configlue.Source.Ssm;

namespace Configlue.Tests;

public sealed class SsmParameterStoreTests
{
    [Test]
    public async Task ReadsMapPaginatedHierarchyIntoNestedFragments()
    {
        var fake = new FakeSsmParameterClient();
        fake.Upsert("/myapp/prod/enabled", "false", "String", 1);
        fake.Upsert("/myapp/prod/retrycount", "7", "String", 2);
        fake.Upsert("/myapp/prod/database/host", "db.example.test", "String", 1);
        fake.Upsert("/myapp/prod/database/port", "6432", "String", 3);
        fake.Upsert("/myapp/prod/plugins", "nord,dracula", "StringList", 1);
        using var source = CreateSource(
            fake,
            new SsmParameterStoreOptions { WithDecryption = false, PageSize = 2 }
        );

        var read = await source.ReadAsync(ConfiglueResourceContext.Default);

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Enabled.Value.ShouldBeFalse();
        read.Value.RetryCount.Value.ShouldBe(7);
        read.Value.Database.Value!.Host.Value.ShouldBe("db.example.test");
        read.Value.Database.Value.Port.Value.ShouldBe(6432);
        read.Value.Plugins.Value!.ShouldBe(["nord", "dracula"]);
        read.Revision.ShouldNotBeNull();
        // Five parameters across pages of two require three calls.
        fake.GetCalls.Count.ShouldBe(3);
        fake.GetCalls.TrueForAll(static call => call.WithDecryption == false).ShouldBeTrue();
    }

    [Test]
    public async Task RootPathNormalizesAndSegmentsMatchCaseInsensitivelyWithSeparators()
    {
        var fake = new FakeSsmParameterClient();
        fake.Upsert("/myapp/prod/retry-count", "9", "String", 1);
        fake.Upsert("/myapp/prod/DATABASE/HOST", "upper.test", "String", 1);
        fake.Upsert("/myapp/prod/label", "spaced", "String", 1);
        // Root without leading/trailing slashes normalizes to the same hierarchy.
        using var source = CreateSource(fake, null, rootPath: "myapp/prod");

        var read = await source.ReadAsync(ConfiglueResourceContext.Default);

        source.RootPath.ShouldBe("/myapp/prod/");
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.RetryCount.Value.ShouldBe(9);
        read.Value.Database.Value!.Host.Value.ShouldBe("upper.test");
        read.Value.Label.Value.ShouldBe("spaced");
    }

    [Test]
    public async Task MissingParametersReturnNotFoundAndUnknownPathsAreIgnored()
    {
        var fake = new FakeSsmParameterClient();
        using var source = CreateSource(fake, null);

        var missing = await source.ReadAsync(ConfiglueResourceContext.Default);
        missing.Status.ShouldBe(StateReadStatus.NotFound);
        missing.Revision.ShouldNotBeNull();

        fake.Upsert("/myapp/prod/unrelated-thing", "x", "String", 1);
        var stillMissing = await source.ReadAsync(ConfiglueResourceContext.Default);
        stillMissing.Status.ShouldBe(StateReadStatus.NotFound);

        fake.Upsert("/myapp/prod/enabled", "true", "String", 2);
        var found = await source.ReadAsync(ConfiglueResourceContext.Default);
        found.Status.ShouldBe(StateReadStatus.Success);
        found.Value!.Enabled.Value.ShouldBeTrue();
        found.Revision.ShouldNotBe(stillMissing.Revision);
    }

    [Test]
    public async Task SecureStringRequiresExplicitDecryptionOptIn()
    {
        var fake = new FakeSsmParameterClient();
        fake.Upsert("/myapp/prod/label", "secret-value", "SecureString", 4);
        using var locked = CreateSource(
            fake,
            new SsmParameterStoreOptions { WithDecryption = false }
        );

        var denied = await locked.ReadAsync(ConfiglueResourceContext.Default);
        denied.Status.ShouldBe(StateReadStatus.InvalidPayload);

        using var unlocked = CreateSource(
            fake,
            new SsmParameterStoreOptions { WithDecryption = true }
        );
        var read = await unlocked.ReadAsync(ConfiglueResourceContext.Default);

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Label.Value.ShouldBe("secret-value");
        // Decryption was requested explicitly for the full read.
        fake.GetCalls[^1].WithDecryption.ShouldBeTrue();
    }

    [Test]
    public async Task StringListKeepsRawValueForStringMembers()
    {
        var fake = new FakeSsmParameterClient();
        fake.Upsert("/myapp/prod/label", "a,b,c", "StringList", 1);
        using var source = CreateSource(fake, null);

        var read = await source.ReadAsync(ConfiglueResourceContext.Default);

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Label.Value.ShouldBe("a,b,c");
    }

    [Test]
    public async Task PollingWatcherObservesChangesUsingMetadataOnly()
    {
        var fake = new FakeSsmParameterClient();
        fake.Upsert("/myapp/prod/enabled", "true", "String", 1);
        fake.Upsert("/myapp/prod/label", "secret-value", "SecureString", 1);
        using var source = CreateSource(
            fake,
            new SsmParameterStoreOptions
            {
                WithDecryption = true,
                WatchChanges = true,
                PollInterval = TimeSpan.FromMilliseconds(50),
                MinPollInterval = TimeSpan.FromMilliseconds(10),
            },
            writable: true,
            watchChanges: true
        );

        var first = await source.ReadAsync(ConfiglueResourceContext.Default);
        fake.GetCalls.Clear();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var watch = source.WaitForChangeAsync(
            ConfiglueResourceContext.Default,
            first.Revision,
            timeout.Token
        );
        fake.Upsert("/myapp/prod/retrycount", "3", "String", 1);
        await watch;

        // Polling never requests decrypted payloads; versions suffice.
        fake.GetCalls.Count.ShouldBeGreaterThan(0);
        fake.GetCalls.TrueForAll(static call => call.WithDecryption == false).ShouldBeTrue();
        var second = await source.ReadAsync(ConfiglueResourceContext.Default);
        second.Revision.ShouldNotBe(first.Revision);
    }

    [Test]
    public async Task WatcherReturnsImmediatelyWhenRevisionIsAlreadyStale()
    {
        var fake = new FakeSsmParameterClient();
        fake.Upsert("/myapp/prod/enabled", "true", "String", 1);
        using var source = CreateSource(
            fake,
            new SsmParameterStoreOptions
            {
                WatchChanges = true,
                PollInterval = TimeSpan.FromMinutes(1),
            },
            watchChanges: true
        );

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // A null observation never matches a real revision, so no polling delay occurs.
        await source.WaitForChangeAsync(ConfiglueResourceContext.Default, null, timeout.Token);
        fake.GetCalls.Count.ShouldBe(1);
    }

    [Test]
    public async Task WritesCreateThenOverwriteWithExplicitFlags()
    {
        var fake = new FakeSsmParameterClient();
        using var source = CreateSource(
            fake,
            new SsmParameterStoreOptions { AllowOverwrite = true },
            writable: true
        );
        var context = ConfiglueResourceContext.Default;

        await source.WriteAsync(
            context,
            new StateWriteRequest<AppSettings.Fragment>(
                new AppSettings.Fragment
                {
                    Enabled = Optional<bool>.Present(false),
                    Database = Optional<DatabaseSettings.Fragment?>.Present(
                        new DatabaseSettings.Fragment
                        {
                            Host = Optional<string>.Present("db.local"),
                        }
                    ),
                }
            )
        );

        fake.PutCalls.Count.ShouldBe(2);
        fake.PutCalls.TrueForAll(static call => call.Overwrite).ShouldBeTrue();
        fake.PutCalls.Select(static call => call.Name)
            .ShouldBe(["/myapp/prod/Database/Host", "/myapp/prod/Enabled"], ignoreOrder: true);

        var read = await source.ReadAsync(context);
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Enabled.Value.ShouldBeFalse();
        read.Value.Database.Value!.Host.Value.ShouldBe("db.local");
    }

    [Test]
    public async Task MustNotExistUsesAtomicCreateAndConflictsWhenPresent()
    {
        var fake = new FakeSsmParameterClient();
        fake.Upsert("/myapp/prod/Enabled", "true", "String", 1);
        using var source = CreateSource(fake, null, writable: true);
        var context = ConfiglueResourceContext.Default;

        await Should.ThrowAsync<StateConflictException>(async () =>
            await source.WriteAsync(
                context,
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) },
                    RevisionCondition.MustNotExist
                )
            )
        );
        fake.PutCalls.Count.ShouldBe(1);
        fake.PutCalls[0].Overwrite.ShouldBeFalse();
    }

    [Test]
    public async Task RevisionMatchWritesAreRejectedBecauseVersionsAreNotCas()
    {
        var fake = new FakeSsmParameterClient();
        fake.Upsert("/myapp/prod/Enabled", "true", "String", 1);
        using var source = CreateSource(fake, null, writable: true);

        var read = await source.ReadAsync(ConfiglueResourceContext.Default);
        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await source.WriteAsync(
                ConfiglueResourceContext.Default,
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) },
                    RevisionCondition.Match(read.Revision!)
                )
            )
        );

        exception.Message.ShouldContain("compare-and-swap");
        fake.PutCalls.Count.ShouldBe(0);
    }

    [Test]
    public async Task ReadOnlySourceExposesNoWriter()
    {
        var fake = new FakeSsmParameterClient();
        using var source = CreateSource(fake, null, writable: false);

        source.Writer.ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await source.WriteAsync(
                ConfiglueResourceContext.Default,
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment { Enabled = Optional<bool>.Present(true) }
                )
            )
        );
    }

    [Test]
    public async Task InjectedAwsClientReadsThroughPublicConstructor()
    {
        using var client = new StubSsmClient(
            new GetParametersByPathResponse
            {
                Parameters =
                [
                    new Parameter
                    {
                        Name = "/myapp/prod/Enabled",
                        Value = "false",
                        Type = ParameterType.String,
                        Version = 1,
                    },
                ],
            }
        );
        using var source = new SsmParameterStoreSource<AppSettings.Fragment>(
            client,
            "/myapp/prod/",
            AppSettings.ConfiglueSchema
        );

        var read = await source.ReadAsync(ConfiglueResourceContext.Default);

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Enabled.Value.ShouldBeFalse();
        client.GetCalls.ShouldBe(1);
    }

    [Test]
    public async Task RegistrationResolvesClientsAndRejectsAmbiguousConfiguration()
    {
        using var client = new StubSsmClient(new GetParametersByPathResponse());
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromSsmParameterStore(
                        new SsmParameterStoreSourceOptions
                        {
                            Id = "ssm-settings",
                            RootPath = "/myapp/prod/",
                            Client = client,
                            Writable = false,
                        }
                    );
                })
            );
        });

        var diagnostics = context.GetRuntimeState<AppSettings>().GetDiagnostics();
        diagnostics.Sources.Count.ShouldBe(1);
        diagnostics.Sources[0].PhysicalOrigin.ShouldBe("ssm:/myapp/prod/");

        Should.Throw<ArgumentException>(() =>
            new ConfiglueSourceSetBuilder().FromSsmParameterStore(
                new SsmParameterStoreSourceOptions
                {
                    RootPath = "/myapp/prod/",
                    Client = client,
                    ClientFactory = _ => client,
                }
            )
        );
    }

    [Test]
    public async Task ThrottlingRetriesThenSucceeds()
    {
        var fake = new FakeSsmParameterClient();
        fake.Upsert("/myapp/prod/enabled", "true", "String", 1);
        fake.ReadFailures.Enqueue(new ThrottlingException("slow down"));
        fake.ReadFailures.Enqueue(new ThrottlingException("slow down"));
        using var source = CreateSource(
            fake,
            new SsmParameterStoreOptions { MaxRetryAttempts = 3, RetryBaseDelay = TimeSpan.Zero }
        );

        var read = await source.ReadAsync(ConfiglueResourceContext.Default);

        read.Status.ShouldBe(StateReadStatus.Success);
        fake.GetCalls.Count.ShouldBe(3);
    }

    [Test]
    public async Task ThrottlingExhaustionSurfacesUnavailableFailure()
    {
        var fake = new FakeSsmParameterClient();
        for (var index = 0; index < 4; index++)
        {
            fake.ReadFailures.Enqueue(new ThrottlingException("slow down"));
        }

        using var source = CreateSource(
            fake,
            new SsmParameterStoreOptions { MaxRetryAttempts = 2, RetryBaseDelay = TimeSpan.Zero }
        );

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await source.ReadAsync(ConfiglueResourceContext.Default)
        );
        fake.GetCalls.Count.ShouldBe(3);
    }

    [Test]
    public async Task CancellationAndDisposeAreHonored()
    {
        var fake = new FakeSsmParameterClient();
        fake.Upsert("/myapp/prod/enabled", "true", "String", 1);
        using var source = CreateSource(
            fake,
            new SsmParameterStoreOptions
            {
                WatchChanges = true,
                PollInterval = TimeSpan.FromMilliseconds(20),
                MinPollInterval = TimeSpan.FromMilliseconds(5),
            },
            watchChanges: true
        );

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await source.ReadAsync(ConfiglueResourceContext.Default, cancelled.Token)
            );
        }

        var read = await source.ReadAsync(ConfiglueResourceContext.Default);
        var watchTask = source
            .WaitForChangeAsync(ConfiglueResourceContext.Default, read.Revision)
            .AsTask();
        source.Dispose();
        // Disposal wakes watchers successfully instead of hanging.
        await watchTask;

        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await source.ReadAsync(ConfiglueResourceContext.Default)
        );
    }

    [Test]
    public async Task DiagnosticsNeverCarrySecretsOrKmsMaterial()
    {
        var fake = new FakeSsmParameterClient();
        fake.Upsert("/myapp/prod/label", "super-secret-value", "SecureString", 1);
        fake.Upsert("/myapp/prod/Label", "super-secret-value", "SecureString", 1);
        using var source = CreateSource(
            fake,
            new SsmParameterStoreOptions
            {
                WithDecryption = true,
                WriteKeyId = "arn:aws:kms:region:1234:key/secret-key-id",
            },
            writable: true
        );

        var read = await source.ReadAsync(ConfiglueResourceContext.Default);
        read.Status.ShouldBe(StateReadStatus.Success);

        source.ToString().ShouldBe("ssm:/myapp/prod/");
        source.Describe().ShouldNotContain("super-secret-value");
        source.Describe().ShouldNotContain("secret-key-id");
        foreach (var provenance in source.LastProvenance)
        {
            provenance.Name.ShouldNotContain("super-secret-value");
            provenance.Type.ShouldBe("SecureString");
        }

        var conflict = await Should.ThrowAsync<StateConflictException>(async () =>
            await source.WriteAsync(
                ConfiglueResourceContext.Default,
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment { Label = Optional<string?>.Present("other") },
                    RevisionCondition.MustNotExist
                )
            )
        );
        conflict.Message.ShouldContain("/myapp/prod/Label");
        conflict.Message.ShouldNotContain("super-secret-value");
        conflict.Message.ShouldNotContain("secret-key-id");
    }

    [Test]
    public async Task NotFoundFallsThroughToLowerPrioritySources()
    {
        var fake = new FakeSsmParameterClient();
        using var ssm = CreateSource(fake, null);
        var ssmSource = new StateSource<AppSettings.Fragment>("ssm", ssm, new StateSourceOptions<AppSettings.Fragment> { Priority = 10 });
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                ssmSource,
                new StateSource<AppSettings.Fragment>("defaults", new FallbackReader(), new StateSourceOptions<AppSettings.Fragment>()),
            ])
        );

        var resolved = await options.ReadAsync();

        resolved.Value!.RetryCount.ShouldBe(11);
    }

    [Test]
    public void ResourceIdIsStablePerRootAndSupportsOverrides()
    {
        var first = CreateSource(new FakeSsmParameterClient(), null);
        var same = CreateSource(new FakeSsmParameterClient(), null);
        var different = CreateSource(new FakeSsmParameterClient(), null, rootPath: "/other/");
        var overridden = new ResourceId("deployment:ssm");
        using var withOverride = new SsmParameterStoreSource<AppSettings.Fragment>(
            new FakeSsmParameterClient(),
            "/myapp/prod/",
            AppSettings.ConfiglueSchema,
            new SsmParameterStoreOptions { FixedResourceId = overridden }
        );

        var context = ConfiglueResourceContext.Default;
        same.GetResourceId(context).ShouldBe(first.GetResourceId(context));
        different.GetResourceId(context).ShouldNotBe(first.GetResourceId(context));
        withOverride.GetResourceId(context).ShouldBe(overridden);
        first.Dispose();
        same.Dispose();
        different.Dispose();
    }

    [Test]
    public void PollIntervalBelowMinimumIsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            CreateSource(
                    new FakeSsmParameterClient(),
                    new SsmParameterStoreOptions
                    {
                        PollInterval = TimeSpan.FromSeconds(1),
                        MinPollInterval = TimeSpan.FromSeconds(5),
                    }
                )
                .Dispose()
        );
    }

    private static SsmParameterStoreSource<AppSettings.Fragment> CreateSource(
        FakeSsmParameterClient fake,
        SsmParameterStoreOptions? options,
        string rootPath = "/myapp/prod/",
        bool writable = false,
        bool? watchChanges = null
    ) =>
        new(
            fake,
            rootPath,
            AppSettings.ConfiglueSchema,
            options,
            writable: writable,
            watchChanges: watchChanges
        );

    private sealed class FakeSsmParameterClient : ISsmParameterClient
    {
        private readonly Dictionary<string, SsmParameterData> _store = new(StringComparer.Ordinal);

        public List<GetCall> GetCalls { get; } = [];

        public List<PutCall> PutCalls { get; } = [];

        public Queue<Exception> ReadFailures { get; } = new();

        public Queue<Exception> WriteFailures { get; } = new();

        public void Upsert(string name, string value, string type, long version) =>
            _store[name] = new SsmParameterData
            {
                Name = name,
                Value = value,
                Type = type,
                Version = version,
                Arn = $"arn:aws:ssm:region:1234:parameter{name}",
                DataType = "text",
            };

        public Task<SsmParameterPage> GetParametersByPathAsync(
            string path,
            bool recursive,
            bool withDecryption,
            string? nextToken,
            int? maxResults,
            CancellationToken cancellationToken
        )
        {
            GetCalls.Add(new GetCall(path, recursive, withDecryption, nextToken, maxResults));
            if (ReadFailures.Count > 0)
            {
                return Task.FromException<SsmParameterPage>(ReadFailures.Dequeue());
            }

            var ordered = _store
                .Values.Where(parameter =>
                    parameter.Name.StartsWith(path, StringComparison.Ordinal)
                )
                .Where(parameter =>
                    recursive || !parameter.Name.Substring(path.Length).Contains('/')
                )
                .OrderBy(static parameter => parameter.Name, StringComparer.Ordinal)
                .ToArray();
            var start = nextToken is null ? 0 : int.Parse(nextToken);
            var size = maxResults ?? 10;
            var page = ordered.Skip(start).Take(size).ToArray();
            var next =
                start + page.Length < ordered.Length ? (start + page.Length).ToString() : null;
            return Task.FromResult(new SsmParameterPage(page, next));
        }

        public Task<SsmPutResult> PutParameterAsync(
            string name,
            string value,
            string type,
            string? keyId,
            bool overwrite,
            string? tier,
            string? dataType,
            CancellationToken cancellationToken
        )
        {
            PutCalls.Add(new PutCall(name, value, type, keyId, overwrite, tier, dataType));
            if (WriteFailures.Count > 0)
            {
                return Task.FromException<SsmPutResult>(WriteFailures.Dequeue());
            }

            if (_store.TryGetValue(name, out var existing))
            {
                if (!overwrite)
                {
                    return Task.FromException<SsmPutResult>(
                        new ParameterAlreadyExistsException($"Parameter {name} already exists.")
                    );
                }

                _store[name] = existing with
                {
                    Value = value,
                    Type = type,
                    Version = existing.Version + 1,
                };
                return Task.FromResult(new SsmPutResult(existing.Version + 1));
            }

            _store[name] = new SsmParameterData
            {
                Name = name,
                Value = value,
                Type = type,
                Version = 1,
                Arn = $"arn:aws:ssm:region:1234:parameter{name}",
                DataType = dataType ?? "text",
            };
            return Task.FromResult(new SsmPutResult(1));
        }

        public sealed record GetCall(
            string Path,
            bool Recursive,
            bool WithDecryption,
            string? NextToken,
            int? MaxResults
        );

        public sealed record PutCall(
            string Name,
            string Value,
            string Type,
            string? KeyId,
            bool Overwrite,
            string? Tier,
            string? DataType
        );
    }

    private sealed class StubSsmClient : AmazonSimpleSystemsManagementClient
    {
        private readonly GetParametersByPathResponse _response;

        public StubSsmClient(GetParametersByPathResponse response)
            : base(
                new BasicAWSCredentials("test-access-key", "test-secret-key"),
                new AmazonSimpleSystemsManagementConfig { ServiceURL = "http://localhost" }
            )
        {
            _response = response;
        }

        public int GetCalls { get; private set; }

        public override Task<GetParametersByPathResponse> GetParametersByPathAsync(
            GetParametersByPathRequest request,
            CancellationToken cancellationToken = default
        )
        {
            GetCalls++;
            return Task.FromResult(_response);
        }
    }

    private sealed class FallbackReader : ISourceReader<AppSettings.Fragment>
    {
        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) =>
            ValueTaskCompat.FromResult(
                StateReadResult<AppSettings.Fragment>.Success(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(11) }
                )
            );
    }
}
