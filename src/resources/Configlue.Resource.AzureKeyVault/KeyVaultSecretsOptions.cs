using System.Text.Json;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;

namespace Configlue.Resource.AzureKeyVault;

/// <summary>Options for a Key Vault secrets source that maps members to secret names.</summary>
/// <remarks>
/// <para>Least-privilege roles:</para>
/// <list type="bullet">
/// <item>Read-only (<see cref="Writable"/> is false): <c>Key Vault Secrets User</c>.</item>
/// <item>Opt-in writes (<see cref="Writable"/> is true): <c>Key Vault Secrets Officer</c>.</item>
/// </list>
/// </remarks>
public sealed class KeyVaultSecretsOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The vault URI (for example <c>https://my-vault.vault.azure.net/</c>).</summary>
    public required Uri VaultUri { get; init; }

    /// <summary>A directly supplied Azure SDK client. It remains caller-owned.</summary>
    public SecretClient? Client { get; init; }

    /// <summary>Resolves an Azure SDK client at context creation, for example from dependency injection.</summary>
    public Func<IServiceProvider?, SecretClient>? ClientFactory { get; init; }

    /// <summary>
    /// A credential used to build a client when <see cref="Client"/> and
    /// <see cref="ClientFactory"/> are both null. Uses <c>DefaultAzureCredential</c> when null.
    /// </summary>
    public TokenCredential? Credential { get; init; }

    /// <summary>Explicit member-path to secret-name mappings.</summary>
    public IReadOnlyList<KeyVaultSecretMapping> Mappings { get; init; } = [];

    /// <summary>
    /// When true, leaf members without an explicit mapping use the deterministic convention
    /// <see cref="KeyVaultSecretName.ToConventionSecretName(string, string?)"/>.
    /// Explicit mappings always win. Convention names that are illegal or ambiguous are rejected.
    /// </summary>
    public bool EnableConventionMapping { get; init; }

    /// <summary>Optional prefix prepended to convention-generated secret names.</summary>
    public string? ConventionPrefix { get; init; }

    /// <summary>
    /// A fixed secret version applied to mappings without their own <see cref="KeyVaultSecretMapping.Version"/>.
    /// Null selects the current version. Fixed-version sources are immutable and expose no watcher.
    /// </summary>
    public string? FixedVersion { get; init; }

    /// <summary>
    /// Optional polling interval for current-version changes. Null disables watching.
    /// This interval is the minimum time between polls; production deployments should use
    /// at least 30 seconds to avoid Key Vault throttling. Fixed-version sources never watch.
    /// </summary>
    public TimeSpan? PollInterval { get; init; }

    /// <summary>Whether this source exposes a writer. Writes are opt-in and default to false.</summary>
    public bool Writable { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Optional scalar conversion override. Types it declines fall back to JSON.</summary>
    public Func<string, Type, object?>? ValueParser { get; init; }

    /// <summary>JSON options used for members without a scalar conversion, such as collections.</summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; init; }

    /// <summary>Additional context is not used; the schema comes from the source registration.</summary>
    public ResourceId? FixedResourceId { get; init; }
}
