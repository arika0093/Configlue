using System.Text;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Secrets;
using Configlue.Provider.Json;
using Configlue.Resource.AzureKeyVault;

namespace Configlue.Tests;

public sealed class KeyVaultSecretsTests
{
    private static readonly Uri VaultUri = new("https://test-vault.vault.azure.net/");

    private static SecretClient CreateSecretClient(IReadOnlyDictionary<string, string> secrets) =>
        new(
            VaultUri,
            new FakeTokenCredential(),
            new SecretClientOptions { Transport = new FakeKeyVaultTransport(secrets) }
        );

    private static KeyVaultSecretsState<AppSettings.Fragment> CreateState(
        FakeSecretsClient client,
        IReadOnlyList<KeyVaultSecretMapping>? mappings = null,
        bool writable = false,
        TimeSpan? pollInterval = null,
        bool convention = false,
        string? conventionPrefix = null,
        string? fixedVersion = null,
        Func<string, Type, object?>? parser = null
    )
    {
        var resolved = KeyVaultSecretsState<AppSettings.Fragment>.ResolveMappings(
            AppSettings.ConfiglueSchema,
            mappings ?? [new KeyVaultSecretMapping("Label", "app-label")],
            convention,
            conventionPrefix,
            fixedVersion
        );
        return new KeyVaultSecretsState<AppSettings.Fragment>(
            client,
            VaultUri,
            AppSettings.ConfiglueSchema,
            resolved,
            writable,
            pollInterval,
            parser,
            null
        );
    }

    [Test]
    public async Task ExplicitMapping_ReadsTypedMembers()
    {
        var client = new FakeSecretsClient();
        client.Set("app-label", "hello-vault");
        client.Set("retry-count", "7");
        client.Set("db-host", "vault-db.example");
        var state = CreateState(
            client,
            [
                new KeyVaultSecretMapping("Label", "app-label"),
                new KeyVaultSecretMapping("RetryCount", "retry-count"),
                new KeyVaultSecretMapping("Database.Host", "db-host"),
            ]
        );

        var read = await state.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Label.Value.ShouldBe("hello-vault");
        read.Value.RetryCount.Value.ShouldBe(7);
        read.Value.Database.Value!.Host.Value.ShouldBe("vault-db.example");
        read.Revision.ShouldNotBeNullOrWhiteSpace();
        read.Revision!.Length.ShouldBe(64);
    }

    [Test]
    public async Task ConventionMapping_DerivesDeterministicNames()
    {
        var client = new FakeSecretsClient();
        client.Set("Label", "convention-label");
        client.Set("Database-Host", "convention-db");
        var state = CreateState(client, [], convention: true);

        var resolved = state.ResolvedMappings.Select(static m => m.SecretName).ToArray();
        resolved.ShouldContain("Label");
        resolved.ShouldContain("Database-Host");

        var read = await state.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Label.Value.ShouldBe("convention-label");
    }

    [Test]
    public void ExplicitMapping_RejectsIllegalSecretNames()
    {
        Should.Throw<ArgumentException>(() => new KeyVaultSecretMapping("Label", "bad.name"));
        Should.Throw<ArgumentException>(() => new KeyVaultSecretMapping("Label", "has space"));
        Should.Throw<ArgumentException>(() =>
            KeyVaultSecretsState<AppSettings.Fragment>.ResolveMappings(
                AppSettings.ConfiglueSchema,
                [new KeyVaultSecretMapping("Missing.Member", "ok-name")],
                false,
                null,
                null
            )
        );
        Should.Throw<ArgumentException>(() =>
            KeyVaultSecretsState<AppSettings.Fragment>.ResolveMappings(
                AppSettings.ConfiglueSchema,
                [
                    new KeyVaultSecretMapping("Label", "dup-secret"),
                    new KeyVaultSecretMapping("RetryCount", "dup-secret"),
                ],
                false,
                null,
                null
            )
        );
    }

    [Test]
    public async Task FixedVersion_IsPinnedAndHasNoWatcher()
    {
        var client = new FakeSecretsClient();
        client.Set("app-label", "v1-label", version: "aaa");
        var state = CreateState(
            client,
            [new KeyVaultSecretMapping("Label", "app-label", "aaa")],
            pollInterval: TimeSpan.FromMilliseconds(20)
        );

        state.IsAllFixedVersion.ShouldBeTrue();
        state.Watcher.ShouldBeNull();

        var first = await state.ReadAsync();
        client.Set("app-label", "v2-label", version: "bbb");
        var second = await state.ReadAsync();

        first.Value!.Label.Value.ShouldBe("v1-label");
        second.Value!.Label.Value.ShouldBe("v1-label");
        first.Revision.ShouldBe(second.Revision);
    }

    [Test]
    public async Task CurrentVersion_FollowsLatestAndPollingDetectsChange()
    {
        var client = new FakeSecretsClient();
        client.Set("app-label", "before");
        var state = CreateState(
            client,
            [new KeyVaultSecretMapping("Label", "app-label")],
            pollInterval: TimeSpan.FromMilliseconds(20)
        );

        state.Watcher.ShouldNotBeNull();
        var first = await state.ReadAsync();
        first.Value!.Label.Value.ShouldBe("before");

        var wait = state.WaitForChangeAsync(ConfiglueResourceContext.Default, first.Revision);
        await Task.Delay(50);
        client.Set("app-label", "after");
        await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        var second = await state.ReadAsync();
        second.Value!.Label.Value.ShouldBe("after");
        second.Revision.ShouldNotBe(first.Revision);
    }

    [Test]
    public async Task MissingSecrets_ProduceSparseNotFound()
    {
        var client = new FakeSecretsClient();
        var state = CreateState(client);

        var empty = await state.ReadAsync();
        empty.Status.ShouldBe(StateReadStatus.NotFound);

        client.Set("app-label", "present");
        var partial = await state.ReadAsync();
        partial.Status.ShouldBe(StateReadStatus.Success);
        partial.Value!.Label.Value.ShouldBe("present");
    }

    [Test]
    public async Task DisabledSecrets_AreTreatedAsMissing()
    {
        var client = new FakeSecretsClient();
        client.Set("app-label", "super-secret-VALUE-777", enabled: false);
        var state = CreateState(client);

        var read = await state.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task Throttling_MapsToUnavailable()
    {
        var client = new FakeSecretsClient { ThrowUnavailable = true };
        var state = CreateState(client);

        var read = await state.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Unavailable);
    }

    [Test]
    public async Task MalformedValues_MapToInvalidPayloadWithoutLeaking()
    {
        const string secretValue = "not-an-int-super-secret-VALUE-888";
        var client = new FakeSecretsClient();
        client.Set("retry-count", secretValue);
        var state = CreateState(client, [new KeyVaultSecretMapping("RetryCount", "retry-count")]);

        var read = await state.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.InvalidPayload);
        read.Revision!.ShouldNotContain(secretValue);
    }

    [Test]
    public async Task ReadOnlySource_HasNoWriter()
    {
        var client = new FakeSecretsClient();
        var readOnly = CreateState(client);
        readOnly.Writer.ShouldBeNull();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await readOnly.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(new AppSettings.Fragment())
            )
        );
    }

    [Test]
    public async Task WritableSource_PersistsPresentMembers()
    {
        var client = new FakeSecretsClient();
        var state = CreateState(
            client,
            [
                new KeyVaultSecretMapping("Label", "app-label"),
                new KeyVaultSecretMapping("RetryCount", "retry-count"),
            ],
            writable: true
        );

        var fragment = new AppSettings.Fragment
        {
            Label = Optional<string?>.Present("written-label"),
            RetryCount = Optional<int>.Present(42),
        };
        var receipt = await state.WriteAsync(new StateWriteRequest<AppSettings.Fragment>(fragment));

        receipt.Revision.ShouldNotBeNullOrWhiteSpace();
        var read = await state.ReadAsync();
        read.Value!.Label.Value.ShouldBe("written-label");
        read.Value.RetryCount.Value.ShouldBe(42);
    }

    [Test]
    public async Task ConditionalWrites_AreRejectedWithoutReadThenWriteRace()
    {
        var client = new FakeSecretsClient();
        client.Set("app-label", "original");
        var state = CreateState(
            client,
            [new KeyVaultSecretMapping("Label", "app-label")],
            writable: true
        );
        var getsBefore = client.GetCalls;

        var fragment = new AppSettings.Fragment
        {
            Label = Optional<string?>.Present("stale-write-attempt"),
        };
        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await state.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(
                    fragment,
                    Condition: RevisionCondition.Match("anything")
                )
            )
        );
        exception.Message.ShouldNotContain("stale-write-attempt");
        client.GetCalls.ShouldBe(getsBefore);
    }

    [Test]
    public async Task ClientFactory_InjectionIsSupported()
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app-label"] = "via-factory",
        };
        var builder = new ConfiglueSourceSetBuilder();
        builder.FromKeyVaultSecrets(
            new KeyVaultSecretsOptions
            {
                VaultUri = VaultUri,
                ClientFactory = _ => CreateSecretClient(secrets),
                Mappings = [new KeyVaultSecretMapping("Label", "app-label")],
            }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromKeyVaultSecrets(
                        new KeyVaultSecretsOptions
                        {
                            VaultUri = VaultUri,
                            ClientFactory = _ => CreateSecretClient(secrets),
                            Mappings = [new KeyVaultSecretMapping("Label", "app-label")],
                        }
                    );
                })
            );
        });
        var read = await context.GetRuntimeState<AppSettings>().GetValueAsync();
        read.Label.ShouldBe("via-factory");
    }

    [Test]
    public void Registration_ValidatesClientAndMappingConfiguration()
    {
        var builder = new ConfiglueSourceSetBuilder();
        var client = CreateSecretClient(new Dictionary<string, string>(StringComparer.Ordinal));

        Should.Throw<ArgumentException>(() =>
            builder.FromKeyVaultSecrets(
                new KeyVaultSecretsOptions
                {
                    VaultUri = VaultUri,
                    Client = client,
                    ClientFactory = _ => client,
                    Mappings = [new KeyVaultSecretMapping("Label", "app-label")],
                }
            )
        );
        Should.Throw<ArgumentException>(() =>
            builder.FromKeyVaultSecrets(
                new KeyVaultSecretsOptions
                {
                    VaultUri = VaultUri,
                    Client = client,
                    Mappings = [],
                }
            )
        );

        var documentBuilder = new ConfiglueSourceSetBuilder();
        Should.Throw<ArgumentException>(() =>
            documentBuilder.FromKeyVaultSecret(
                new KeyVaultSecretSourceOptions
                {
                    VaultUri = VaultUri,
                    SecretName = "bad.name",
                    Client = client,
                    Codec = StateCodecBinding.Typed(new JsonStateCodec<AppSettings.Fragment>()),
                }
            )
        );
    }

    [Test]
    public async Task CancellationAndDispose_AreHonored()
    {
        var client = new FakeSecretsClient();
        client.Set("app-label", "value");
        var state = CreateState(
            client,
            [new KeyVaultSecretMapping("Label", "app-label")],
            pollInterval: TimeSpan.FromMilliseconds(20)
        );

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await state.ReadAsync(ConfiglueResourceContext.Default, cancelled.Token)
        );

        state.Dispose();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await state.ReadAsync());
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await state.WaitForChangeAsync(ConfiglueResourceContext.Default, "rev").AsTask()
        );
    }

    [Test]
    public async Task Redaction_IsSystematicAcrossDiagnostics()
    {
        const string secretValue = "super-secret-VALUE-12345";
        var client = new FakeSecretsClient();
        client.Set("app-label", secretValue);
        client.Set("retry-count", "3");
        var state = CreateState(
            client,
            [
                new KeyVaultSecretMapping("Label", "app-label"),
                new KeyVaultSecretMapping("RetryCount", "retry-count"),
            ]
        );

        var read = await state.ReadAsync();
        read.Revision!.ShouldNotContain(secretValue);
        read.PhysicalOrigin!.ShouldNotContain(secretValue);
        read.PhysicalOrigin.ShouldBe("keyvault:test-vault.vault.azure.net");
        state.ToString()!.ShouldNotContain(secretValue);

        var malformed = new FakeSecretsClient();
        malformed.Set("retry-count", secretValue);
        var malformedState = CreateState(
            malformed,
            [new KeyVaultSecretMapping("RetryCount", "retry-count")]
        );
        var invalid = await malformedState.ReadAsync();
        invalid.Revision!.ShouldNotContain(secretValue);

        var resource = new KeyVaultSecretResource(client, VaultUri, "app-label");
        resource.ToString().ShouldNotContain(secretValue);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromKeyVaultSecrets(
                        new KeyVaultSecretsOptions
                        {
                            VaultUri = VaultUri,
                            Client = CreateSecretClient(
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["app-label"] = secretValue,
                                }
                            ),
                            Mappings = [new KeyVaultSecretMapping("Label", "app-label")],
                        }
                    );
                })
            );
        });
        var inspection = context.GetRuntimeState<AppSettings>();
        var check = inspection.Check();
        await foreach (var source in check)
        {
            source.ToString()!.ShouldNotContain(secretValue);
        }

        var checkResult = await check.Result;
        checkResult.ToString()!.ShouldNotContain(secretValue);
    }

    private sealed class FakeSecretsClient : IKeyVaultSecretClient
    {
        private readonly Dictionary<string, List<StoredSecret>> _secrets = new(
            StringComparer.Ordinal
        );
        private int _versionCounter;

        public int GetCalls { get; private set; }

        public bool ThrowUnavailable { get; set; }

        public void Set(string name, string value, string? version = null, bool enabled = true)
        {
            var resolvedVersion = version ?? $"v{++_versionCounter}-{name}";
            if (!_secrets.TryGetValue(name, out var versions))
            {
                versions = [];
                _secrets[name] = versions;
            }

            versions.RemoveAll(v =>
                string.Equals(v.Version, resolvedVersion, StringComparison.Ordinal)
            );
            versions.Add(new StoredSecret(value, resolvedVersion, enabled));
        }

        public ValueTask<KeyVaultSecretResult> GetSecretAsync(
            string secretName,
            string? version,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            if (ThrowUnavailable)
            {
                return ValueTaskCompat.FromException<KeyVaultSecretResult>(
                    new KeyVaultSecretUnavailableException($"Secret '{secretName}' unavailable.")
                );
            }

            if (!_secrets.TryGetValue(secretName, out var versions) || versions.Count == 0)
            {
                return ValueTaskCompat.FromException<KeyVaultSecretResult>(
                    new KeyVaultSecretNotFoundException($"Secret '{secretName}' not found.")
                );
            }

            StoredSecret stored;
            if (version is null)
            {
                stored = versions[^1];
            }
            else
            {
                var match = versions.FirstOrDefault(v =>
                    string.Equals(v.Version, version, StringComparison.Ordinal)
                );
                if (match is null)
                {
                    return ValueTaskCompat.FromException<KeyVaultSecretResult>(
                        new KeyVaultSecretNotFoundException($"Secret '{secretName}' not found.")
                    );
                }

                stored = match;
            }

            var metadata = new KeyVaultSecretMetadata(secretName, stored.Version, stored.Enabled);
            return ValueTaskCompat.FromResult(new KeyVaultSecretResult(stored.Value, metadata));
        }

        public ValueTask<KeyVaultSecretMetadata> SetSecretAsync(
            string secretName,
            string secretValue,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowUnavailable)
            {
                return ValueTaskCompat.FromException<KeyVaultSecretMetadata>(
                    new KeyVaultSecretUnavailableException($"Secret '{secretName}' unavailable.")
                );
            }

            var version = $"v{++_versionCounter}-{secretName}-set";
            if (!_secrets.TryGetValue(secretName, out var versions))
            {
                versions = [];
                _secrets[secretName] = versions;
            }

            versions.Add(new StoredSecret(secretValue, version, true));
            return ValueTaskCompat.FromResult(
                new KeyVaultSecretMetadata(secretName, version, true)
            );
        }

        private sealed record StoredSecret(string Value, string Version, bool Enabled);
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken
        ) => new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken
        ) => new(GetToken(requestContext, cancellationToken));
    }

    /// <summary>
    /// Serves canned Key Vault secret payloads over the Azure SDK pipeline so
    /// registration-level tests exercise the public <c>SecretClient</c> surface.
    /// </summary>
    private sealed class FakeKeyVaultTransport : HttpPipelineTransport
    {
        private readonly IReadOnlyDictionary<string, string> _secrets;

        public FakeKeyVaultTransport(IReadOnlyDictionary<string, string> secrets) =>
            _secrets = secrets;

        public override Request CreateRequest() => new FakeKeyVaultRequest();

        public override void Process(HttpMessage message) =>
            message.Response = CreateResponse(message);

        public override ValueTask ProcessAsync(HttpMessage message)
        {
            message.Response = CreateResponse(message);
            return default;
        }

        private FakeKeyVaultResponse CreateResponse(HttpMessage message)
        {
            var path = message.Request.Uri?.ToUri().AbsolutePath ?? string.Empty;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (
                segments.Length >= 2
                && string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase)
                && _secrets.TryGetValue(Uri.UnescapeDataString(segments[1]), out var value)
            )
            {
                var name = Uri.UnescapeDataString(segments[1]);
                var version = segments.Length >= 3 ? Uri.UnescapeDataString(segments[2]) : "v1";
                var body =
                    "{\"value\":\""
                    + EscapeJson(value)
                    + "\",\"id\":\""
                    + VaultUri.AbsoluteUri.TrimEnd('/')
                    + "/secrets/"
                    + EscapeJson(name)
                    + "/"
                    + EscapeJson(version)
                    + "\",\"attributes\":{\"enabled\":true}}";
                return new FakeKeyVaultResponse(200, body);
            }

            return new FakeKeyVaultResponse(
                404,
                "{\"error\":{\"code\":\"SecretNotFound\",\"message\":\"Secret not found.\"}}"
            );
        }

        private static string EscapeJson(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        builder.Append(character);
                        break;
                }
            }

            return builder.ToString();
        }
    }

    private sealed class FakeKeyVaultRequest : Request
    {
        private readonly Dictionary<string, string> _headers = new(
            StringComparer.OrdinalIgnoreCase
        );

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose() { }

        protected override void AddHeader(string name, string value) => _headers[name] = value;

        protected override bool ContainsHeader(string name) => _headers.ContainsKey(name);

        protected override IEnumerable<HttpHeader> EnumerateHeaders() =>
            _headers.Select(static pair => new HttpHeader(pair.Key, pair.Value));

        protected override bool RemoveHeader(string name) => _headers.Remove(name);

        protected override bool TryGetHeader(string name, out string value)
        {
            if (_headers.TryGetValue(name, out var found))
            {
                value = found;
                return true;
            }

            value = string.Empty;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            if (_headers.TryGetValue(name, out var value))
            {
                values = [value];
                return true;
            }

            values = [];
            return false;
        }
    }

    private sealed class FakeKeyVaultResponse : Response
    {
        public FakeKeyVaultResponse(int status, string body)
        {
            Status = status;
            ContentStream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        }

        public override int Status { get; }

        public override string ReasonPhrase => Status == 200 ? "OK" : "Not Found";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = string.Empty;

        protected override bool ContainsHeader(string name) => false;

        protected override bool TryGetHeader(string name, out string value)
        {
            value = string.Empty;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = [];
            return false;
        }

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];

        public override void Dispose() => ContentStream?.Dispose();
    }
}
