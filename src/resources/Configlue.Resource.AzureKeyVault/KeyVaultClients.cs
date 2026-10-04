using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Configlue.Resource.AzureKeyVault;

/// <summary>Creates Azure SDK clients for Key Vault secrets.</summary>
/// <remarks>
/// <para>Least-privilege roles, documented separately for read and write:</para>
/// <list type="bullet">
/// <item>Read-only: <c>Key Vault Secrets User</c> (data-plane <c>Microsoft.KeyVault/vaults/secrets/getSecret/action</c>).</item>
/// <item>Opt-in writes: <c>Key Vault Secrets Officer</c> (adds <c>setSecret</c>). Prefer a separate vault or scoped assignment for writers.</item>
/// </list>
/// <para>Management-plane roles such as Owner/Contributor are not required and should not be granted.</para>
/// </remarks>
public static class KeyVaultClients
{
    /// <summary>
    /// Creates an externally owned <c>SecretClient</c> for a vault. Uses
    /// <c>DefaultAzureCredential</c> when no credential is supplied.
    /// </summary>
    public static SecretClient CreateSecretClient(Uri vaultUri, TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(vaultUri);
        return new SecretClient(vaultUri, credential ?? new DefaultAzureCredential());
    }

    /// <summary>Creates the internal transport over the Azure SDK client.</summary>
    internal static IKeyVaultSecretClient CreateAdapter(SecretClient client) =>
        new SecretClientAdapter(client);

    /// <summary>
    /// Creates the internal transport directly from a vault URI and optional credential.
    /// The underlying <c>SecretClient</c> remains caller-owned via the adapter.
    /// </summary>
    internal static IKeyVaultSecretClient Create(
        Uri vaultUri,
        TokenCredential? credential = null
    ) => new SecretClientAdapter(CreateSecretClient(vaultUri, credential));

    /// <summary>Returns a redacted vault origin for diagnostics; never includes secret material.</summary>
    public static string GetPhysicalOrigin(Uri vaultUri)
    {
        ArgumentNullException.ThrowIfNull(vaultUri);
        return $"keyvault:{vaultUri.Host}";
    }
}
