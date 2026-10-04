namespace Configlue.Resource.AzureAppConfiguration;

/// <summary>
/// Deterministic mapping between Azure App Configuration keys and Configlue member paths.
/// </summary>
/// <remarks>
/// Feature flags (<c>.appconfig.featureflag/*</c> and the feature-flag content type) are never
/// treated as ordinary model members in v1. Key Vault references are preserved as opaque values.
/// </remarks>
public static class AzureAppConfigurationKeyMapper
{
    /// <summary>The key prefix used by Azure feature flags.</summary>
    public const string FeatureFlagKeyPrefix = ".appconfig.featureflag/";

    /// <summary>The content type used by Azure feature flags.</summary>
    public const string FeatureFlagContentType =
        "application/vnd.microsoft.appconfig.ff+json;charset=utf-8";

    /// <summary>The content type used by Azure Key Vault references.</summary>
    public const string KeyVaultReferenceContentType =
        "application/vnd.microsoft.appconfig.keyvaultref+json";

    /// <summary>Whether the entry is a feature flag and must not become a model member.</summary>
    public static bool IsFeatureFlag(AppConfigurationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return IsFeatureFlagKey(entry.Key) || IsFeatureFlagContentType(entry.ContentType);
    }

    /// <summary>Whether the key belongs to the feature-flag namespace.</summary>
    public static bool IsFeatureFlagKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.StartsWith(FeatureFlagKeyPrefix, StringComparison.Ordinal);
    }

    /// <summary>Whether the content type marks a feature flag.</summary>
    public static bool IsFeatureFlagContentType(string? contentType) =>
        string.Equals(contentType, FeatureFlagContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the content type marks a Key Vault reference.</summary>
    public static bool IsKeyVaultReference(string? contentType) =>
        contentType is not null
        && contentType.StartsWith(KeyVaultReferenceContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tests a key against an App Configuration key filter.
    /// Supports <c>*</c> (all), a trailing <c>*</c> prefix match, comma-separated lists,
    /// and exact matches. Matching is ordinal and case-sensitive.
    /// </summary>
    public static bool MatchesKeyFilter(string key, string? keyFilter)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (keyFilter is null || string.IsNullOrWhiteSpace(keyFilter) || keyFilter == "*")
        {
            return true;
        }

        foreach (var part in keyFilter.Split(','))
        {
            var filter = part.Trim();
            if (filter.Length == 0)
            {
                continue;
            }

            if (filter == "*")
            {
                return true;
            }

            if (filter.EndsWith("*", StringComparison.Ordinal))
            {
                var prefix = filter[..^1];
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (string.Equals(key, filter, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Maps a configuration key to a deterministic member path.
    /// </summary>
    /// <param name="key">The full App Configuration key.</param>
    /// <param name="trimPrefix">An optional prefix stripped before mapping, like the .NET provider's trim-prefix.</param>
    /// <param name="memberPath">The resulting path segments.</param>
    /// <returns>False when the key does not start with <paramref name="trimPrefix"/> and is excluded.</returns>
    /// <exception cref="FormatException">The mapped path contains an empty segment.</exception>
    public static bool TryMapToMemberPath(string key, string? trimPrefix, out string[] memberPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var remainder = key;
        if (trimPrefix is { Length: > 0 })
        {
            if (!remainder.StartsWith(trimPrefix, StringComparison.Ordinal))
            {
                memberPath = [];
                return false;
            }

            remainder = remainder[trimPrefix.Length..];
        }

        if (remainder.Length == 0)
        {
            throw new FormatException(
                $"App Configuration key '{key}' maps to an empty member path."
            );
        }

        var segments = remainder.Split([':', '/'], StringSplitOptions.None);
        if (segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new FormatException(
                $"App Configuration key '{key}' contains an empty path segment."
            );
        }

        memberPath = segments;
        return true;
    }

    /// <summary>Maps a member path back to a configuration key.</summary>
    public static string ToConfigurationKey(string[] memberPath, string? trimPrefix)
    {
        ArgumentNullException.ThrowIfNull(memberPath);
        if (memberPath.Length == 0)
        {
            throw new ArgumentException(
                "A member path must contain at least one segment.",
                nameof(memberPath)
            );
        }

        if (memberPath.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A member path cannot contain empty segments.",
                nameof(memberPath)
            );
        }

        return (trimPrefix ?? string.Empty) + string.Join(":", memberPath);
    }
}
