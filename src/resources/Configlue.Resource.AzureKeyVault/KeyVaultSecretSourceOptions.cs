using Azure.Core;
using Configlue.Codecs;

namespace Configlue.Resource.AzureKeyVault;

/// <summary>Options for registering a source backed by one Key Vault secret document.</summary>
/// <remarks>
/// The single secret contains a serialized Configlue fragment routed through the normal
/// Resource + Codec pipeline. Least-privilege roles: read-only uses
/// <c>Key Vault Secrets User</c>; opt-in writes require <c>Key Vault Secrets Officer</c>.
/// </remarks>
public sealed class KeyVaultSecretSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The vault URI (for example <c>https://my-vault.vault.azure.net/</c>).</summary>
    public required Uri VaultUri { get; init; }

    /// <summary>The secret name holding the serialized document.</summary>
    public required string SecretName { get; init; }

    /// <summary>A fixed secret version, or null for the current version.</summary>
    public string? SecretVersion { get; init; }

    /// <summary>A directly supplied client. It remains caller-owned.</summary>
    public IKeyVaultSecretClient? Client { get; init; }

    /// <summary>Resolves a client at context creation, for example from dependency injection.</summary>
    public Func<IServiceProvider?, IKeyVaultSecretClient>? ClientFactory { get; init; }

    /// <summary>
    /// A credential used to build a client when <see cref="Client"/> and
    /// <see cref="ClientFactory"/> are both null. Uses <c>DefaultAzureCredential</c> when null.
    /// </summary>
    public TokenCredential? Credential { get; init; }

    /// <summary>The codec for the serialized secret document.</summary>
    public required StateCodecBinding Codec { get; init; }

    /// <summary>Resource identity settings.</summary>
    public KeyVaultSecretResourceOptions? ResourceOptions { get; init; }

    /// <summary>
    /// Optional polling interval for current-version changes. Null disables watching.
    /// Fixed versions are immutable and never watch, even when this is set.
    /// </summary>
    public TimeSpan? PollInterval { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer. Writes are opt-in and default to false.</summary>
    public bool Writable { get; init; }

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }
}
