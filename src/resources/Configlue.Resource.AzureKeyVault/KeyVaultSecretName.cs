namespace Configlue.Resource.AzureKeyVault;

/// <summary>
/// Validates Azure Key Vault secret names and documents the deterministic mapping
/// between Configlue member paths and secret names.
/// </summary>
/// <remarks>
/// <para>
/// Key Vault secret names must match <c>^[0-9a-zA-Z-]{1,127}$</c>.
/// Arbitrary Configlue member paths cannot be used verbatim as secret names because
/// paths contain <c>.</c> separators (and may contain other characters) that are illegal
/// in Key Vault. Explicit mappings must therefore name every secret.
/// </para>
/// <para>
/// The optional convention mapping is strictly deterministic:
/// <c>string.Join("-", propertyPath.Split('.'))</c> with an optional
/// <c>prefix + "-"</c> prepended. Member-name casing is preserved verbatim.
/// The converted name is then validated against the Key Vault rule above.
/// Any path that does not convert to a legal, unambiguous secret name must use an
/// explicit <see cref="KeyVaultSecretMapping"/> instead. Explicit mappings always win
/// over convention-generated names.
/// </para>
/// </remarks>
public static class KeyVaultSecretName
{
    /// <summary>The maximum length of a Key Vault secret name.</summary>
    public const int MaxLength = 127;

    /// <summary>Tests whether a secret name satisfies the Key Vault rule.</summary>
    public static bool IsValid(string? secretName)
    {
        if (secretName is null || secretName.Length == 0 || secretName.Length > MaxLength)
        {
            return false;
        }

        foreach (var ch in secretName)
        {
            var isDigit = ch >= '0' && ch <= '9';
            var isUpper = ch >= 'A' && ch <= 'Z';
            var isLower = ch >= 'a' && ch <= 'z';
            if (!(isDigit || isUpper || isLower || ch == '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Validates a secret name, throwing when it violates the Key Vault rule.</summary>
    public static string Validate(string? secretName, string paramName = "secretName")
    {
        if (!IsValid(secretName))
        {
            throw new ArgumentException(
                "An Azure Key Vault secret name must be 1-127 characters of letters, digits, and '-'. "
                    + "Do not use Configlue member paths verbatim; map each member to a legal secret name explicitly.",
                paramName
            );
        }

        return secretName!;
    }

    /// <summary>
    /// Converts a canonical dotted property path (for example <c>Database.Host</c>) to a
    /// convention secret name by joining segments with <c>-</c>.
    /// </summary>
    public static string ToConventionSecretName(string propertyPath, string? prefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        if (prefix is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
            Validate(prefix, nameof(prefix));
        }

        var segments = propertyPath.Split('.');
        if (segments.Length == 0 || segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A property path must contain non-empty '.'-separated segments.",
                nameof(propertyPath)
            );
        }

        var joined = string.Join("-", segments);
        var candidate = prefix is null ? joined : prefix + "-" + joined;
        return Validate(candidate, nameof(propertyPath));
    }
}
