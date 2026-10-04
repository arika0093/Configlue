using System.Text;
using Configlue.Provider.Json;
using Configlue.Resource.AzureKeyVault;

namespace Configlue.Tests;

public sealed class KeyVaultSecretResourceTests
{
    private static readonly Uri VaultUri = new("https://test-vault.vault.azure.net/");

    [Test]
    public void SecretName_RejectsIllegalNames()
    {
        KeyVaultSecretName.IsValid("db-password").ShouldBeTrue();
        KeyVaultSecretName.IsValid("ABC-123").ShouldBeTrue();
        KeyVaultSecretName.IsValid("Database.Host").ShouldBeFalse();
        KeyVaultSecretName.IsValid("with_underscore").ShouldBeFalse();
        KeyVaultSecretName.IsValid(string.Empty).ShouldBeFalse();
        KeyVaultSecretName.IsValid(new string('a', 128)).ShouldBeFalse();
        KeyVaultSecretName.IsValid(new string('a', 127)).ShouldBeTrue();

        Should.Throw<ArgumentException>(() => KeyVaultSecretName.Validate("Database.Host"));
        Should.Throw<ArgumentException>(() => KeyVaultSecretName.Validate("has space"));
        KeyVaultSecretName.ToConventionSecretName("Database.Host").ShouldBe("Database-Host");
        KeyVaultSecretName
            .ToConventionSecretName("Database.Host", "app")
            .ShouldBe("app-Database-Host");
    }

    [Test]
    public async Task ReadAsync_ReturnsUtf8BytesAndVersionRevision()
    {
        var client = new FakeKeyVaultClient();
        client.AddSecret("settings-doc", "v1", """{"Enabled":false}""");
        var resource = new KeyVaultSecretResource(client, VaultUri, "settings-doc");

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        Encoding.UTF8.GetString(result.Content.ToArray()).ShouldBe("""{"Enabled":false}""");
        result.Revision.ShouldBe("keyvault:v1");
    }

    [Test]
    public async Task ReadAsync_FixedVersionIsPinned()
    {
        var client = new FakeKeyVaultClient();
        client.AddSecret("settings-doc", "v1", "first");
        client.AddSecret("settings-doc", "v2", "second");
        var fixedResource = new KeyVaultSecretResource(client, VaultUri, "settings-doc", "v1");
        var currentResource = new KeyVaultSecretResource(client, VaultUri, "settings-doc");

        fixedResource.IsFixedVersion.ShouldBeTrue();
        currentResource.IsFixedVersion.ShouldBeFalse();

        var fixedRead = await fixedResource.ReadAsync();
        var currentRead = await currentResource.ReadAsync();

        Encoding.UTF8.GetString(fixedRead.Content.ToArray()).ShouldBe("first");
        fixedRead.Revision.ShouldBe("keyvault:v1");
        Encoding.UTF8.GetString(currentRead.Content.ToArray()).ShouldBe("second");
        currentRead.Revision.ShouldBe("keyvault:v2");
    }

    [Test]
    public async Task ReadAsync_MapsMissingToNotFound()
    {
        var client = new FakeKeyVaultClient();
        var resource = new KeyVaultSecretResource(client, VaultUri, "missing-secret");

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.NotFound);
        (result.Content.Length == 0).ShouldBeTrue();
    }

    [Test]
    public async Task ReadAsync_MapsDisabledToNotFound()
    {
        var client = new FakeKeyVaultClient();
        client.AddSecret("disabled-secret", "v1", "super-secret-VALUE-999", enabled: false);
        var resource = new KeyVaultSecretResource(client, VaultUri, "disabled-secret");

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task ReadAsync_MapsThrottlingToUnavailable()
    {
        var client = new FakeKeyVaultClient { ThrowUnavailable = true };
        var resource = new KeyVaultSecretResource(client, VaultUri, "any-secret");

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Unavailable);
    }

    [Test]
    public async Task ReadPipelineAsync_ReturnsContentAndRevision()
    {
        var client = new FakeKeyVaultClient();
        client.AddSecret("settings-doc", "v3", "pipeline-bytes");
        var resource = new KeyVaultSecretResource(client, VaultUri, "settings-doc");

        await using var result = await resource.ReadPipelineAsync();
        var sequence = await result.ReadAllAsync();
        var bytes = System.Buffers.BuffersExtensions.ToArray(sequence);
        result.Content!.AdvanceTo(sequence.End);

        result.Status.ShouldBe(StateReadStatus.Success);
        Encoding.UTF8.GetString(bytes).ShouldBe("pipeline-bytes");
        result.Revision.ShouldBe("keyvault:v3");
    }

    [Test]
    public async Task WriteAsync_CreatesNewVersionUnconditionally()
    {
        var client = new FakeKeyVaultClient();
        var resource = new KeyVaultSecretResource(client, VaultUri, "settings-doc");

        var written = await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("v-one")));

        written.Revision.ShouldBe("keyvault:v1");
        var read = await resource.ReadAsync();
        Encoding.UTF8.GetString(read.Content.ToArray()).ShouldBe("v-one");

        var rewritten = await resource.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes("v-two"))
        );
        rewritten.Revision.ShouldBe("keyvault:v2");
    }

    [Test]
    public async Task WriteAsync_RejectsConditionalWritesWithoutRace()
    {
        var client = new FakeKeyVaultClient();
        client.AddSecret("settings-doc", "v1", "original");
        var resource = new KeyVaultSecretResource(client, VaultUri, "settings-doc");
        var beforeGets = client.GetCalls;

        var matchException = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(
                    Encoding.UTF8.GetBytes("stale-write"),
                    Condition: RevisionCondition.Match("keyvault:v1")
                )
            )
        );
        matchException.Message.ShouldNotContain("stale-write");
        client.GetCalls.ShouldBe(beforeGets);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(
                    Encoding.UTF8.GetBytes("create-only"),
                    Condition: RevisionCondition.MustNotExist
                )
            )
        );
    }

    [Test]
    public async Task DocumentMode_RoutesThroughResourceAndCodecPipeline()
    {
        var client = new FakeKeyVaultClient();
        var resource = new KeyVaultSecretResource(client, VaultUri, "app-settings");
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var serialized = new SerializedSource<AppSettings.Fragment>(
            resource,
            StateCodecBinding.Typed(codec),
            writer: resource
        );
        var fragment = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Label = Optional<string?>.Present("from-vault"),
        };
        await serialized.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(fragment)
        );

        var read = await serialized.ReadAsync(ConfiglueResourceContext.Default);

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.Enabled.Value.ShouldBeFalse();
        read.Value.Label.Value.ShouldBe("from-vault");
        read.Revision!.ShouldStartWith("keyvault:");
        serialized.Watcher.ShouldBeNull();
    }

    [Test]
    public async Task Cancellation_IsHonored()
    {
        var client = new FakeKeyVaultClient();
        client.AddSecret("settings-doc", "v1", "value");
        var resource = new KeyVaultSecretResource(client, VaultUri, "settings-doc");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.ReadAsync(ConfiglueResourceContext.Default, cancelled.Token)
        );
    }

    [Test]
    public async Task Dispose_BlocksFurtherOperations()
    {
        var client = new FakeKeyVaultClient();
        client.AddSecret("settings-doc", "v1", "value");
        var resource = new KeyVaultSecretResource(client, VaultUri, "settings-doc");
        resource.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await resource.ReadAsync());
    }

    [Test]
    public void ToStringAndProvenance_NeverLeakSecretValues()
    {
        const string secretValue = "super-secret-VALUE-12345";
        var client = new FakeKeyVaultClient();
        client.AddSecret("settings-doc", "v1", secretValue);
        var resource = new KeyVaultSecretResource(client, VaultUri, "settings-doc");

        resource.ToString().ShouldNotContain(secretValue);
        resource.GetResourceId(ConfiglueResourceContext.Default).ToString().ShouldNotContain(secretValue);
        KeyVaultClients.GetPhysicalOrigin(VaultUri).ShouldBe("keyvault:test-vault.vault.azure.net");
        KeyVaultClients.GetPhysicalOrigin(VaultUri).ShouldNotContain(secretValue);
    }

    [Test]
    public async Task RevisionAndExceptions_NeverLeakSecretValues()
    {
        const string secretValue = "super-secret-VALUE-12345";
        var client = new FakeKeyVaultClient();
        client.AddSecret("settings-doc", "v1", secretValue);
        var resource = new KeyVaultSecretResource(client, VaultUri, "settings-doc");

        var read = await resource.ReadAsync();
        read.Revision!.ShouldNotContain(secretValue);

        client.ThrowUnavailable = true;
        var unavailable = await resource.ReadAsync();
        (unavailable.Revision ?? string.Empty).ShouldNotContain(secretValue);

        client.ThrowUnavailable = false;
        client.ThrowNotFound = true;
        var missing = await resource.ReadAsync();
        (missing.Revision ?? string.Empty).ShouldNotContain(secretValue);
    }

    [Test]
    public void ResourceId_IsStableAndVersionAware()
    {
        var client = new FakeKeyVaultClient();
        var first = new KeyVaultSecretResource(client, VaultUri, "settings-doc");
        var same = new KeyVaultSecretResource(client, VaultUri, "settings-doc");
        var other = new KeyVaultSecretResource(client, VaultUri, "other-doc");
        var fixedVersion = new KeyVaultSecretResource(client, VaultUri, "settings-doc", "v1");
        var context = ConfiglueResourceContext.Default;

        same.GetResourceId(context).ShouldBe(first.GetResourceId(context));
        other.GetResourceId(context).ShouldNotBe(first.GetResourceId(context));
        fixedVersion.GetResourceId(context).ShouldNotBe(first.GetResourceId(context));
    }

    private sealed class FakeKeyVaultClient : IKeyVaultSecretClient
    {
        private readonly Dictionary<string, List<FakeVersion>> _secrets = new(StringComparer.Ordinal);
        private int _versionCounter;

        public int GetCalls { get; private set; }

        public bool ThrowUnavailable { get; set; }

        public bool ThrowNotFound { get; set; }

        public void AddSecret(string name, string version, string value, bool enabled = true)
        {
            if (!_secrets.TryGetValue(name, out var versions))
            {
                versions = [];
                _secrets[name] = versions;
            }

            versions.Add(new FakeVersion(version, value, enabled));
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

            if (ThrowNotFound)
            {
                return ValueTaskCompat.FromException<KeyVaultSecretResult>(
                    new KeyVaultSecretNotFoundException($"Secret '{secretName}' not found.")
                );
            }

            if (!_secrets.TryGetValue(secretName, out var versions) || versions.Count == 0)
            {
                return ValueTaskCompat.FromException<KeyVaultSecretResult>(
                    new KeyVaultSecretNotFoundException($"Secret '{secretName}' not found.")
                );
            }

            FakeVersion selected;
            if (version is null)
            {
                selected = versions[^1];
            }
            else
            {
                var match = versions.FirstOrDefault(v => v.Version == version);
                if (match is null)
                {
                    return ValueTaskCompat.FromException<KeyVaultSecretResult>(
                        new KeyVaultSecretNotFoundException($"Secret '{secretName}' not found.")
                    );
                }

                selected = match;
            }

            var metadata = new KeyVaultSecretMetadata(
                secretName,
                selected.Version,
                selected.Enabled
            );
            return ValueTaskCompat.FromResult(new KeyVaultSecretResult(selected.Value, metadata));
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

            _versionCounter++;
            var version = $"v{_versionCounter}";
            if (!_secrets.TryGetValue(secretName, out var versions))
            {
                versions = [];
                _secrets[secretName] = versions;
            }

            versions.Add(new FakeVersion(version, secretValue, true));
            return ValueTaskCompat.FromResult(new KeyVaultSecretMetadata(secretName, version, true));
        }

        private sealed record FakeVersion(string Version, string Value, bool Enabled);
    }
}
