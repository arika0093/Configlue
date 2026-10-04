namespace Configlue.Resource.AzureKeyVault;

/// <summary>Base error for Azure Key Vault secret operations. Never carries secret values.</summary>
public class KeyVaultSecretException : Exception
{
    /// <summary>Creates an exception with a redacted message.</summary>
    public KeyVaultSecretException(string message)
        : base(message) { }

    /// <summary>Creates an exception with a redacted message and inner error.</summary>
    public KeyVaultSecretException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>The secret name or version does not exist, is disabled, or the caller lacks visibility.</summary>
/// <remarks>Messages carry only the vault host and secret name; never secret values.</remarks>
public sealed class KeyVaultSecretNotFoundException : KeyVaultSecretException
{
    /// <summary>Creates a redacted not-found error.</summary>
    public KeyVaultSecretNotFoundException(string message)
        : base(message) { }

    /// <summary>Creates a redacted not-found error.</summary>
    public KeyVaultSecretNotFoundException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>The request was throttled or the service is transiently unavailable.</summary>
/// <remarks>Messages carry only the vault host and secret name; never secret values.</remarks>
public sealed class KeyVaultSecretUnavailableException : KeyVaultSecretException
{
    /// <summary>Creates a redacted transient error.</summary>
    public KeyVaultSecretUnavailableException(string message)
        : base(message) { }

    /// <summary>Creates a redacted transient error.</summary>
    public KeyVaultSecretUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
