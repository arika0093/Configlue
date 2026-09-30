namespace Configlue.Resources;

/// <summary>Combines a host path resolver with per-location overrides.</summary>
public sealed class ConfiglueHostPathProfile : IConfiglueHostPaths
{
    private readonly IConfiglueHostPaths? _fallback;
    private readonly IReadOnlyDictionary<
        ConfiglueStandardLocation,
        Func<string, string?>
    > _overrides;

    private ConfiglueHostPathProfile(
        IConfiglueHostPaths? fallback,
        IReadOnlyDictionary<ConfiglueStandardLocation, Func<string, string?>> overrides
    )
    {
        _fallback = fallback;
        _overrides = overrides;
    }

    /// <summary>The built-in Windows/macOS/Linux host profile.</summary>
    public static ConfiglueHostPathProfile Default { get; } =
        new(null, new Dictionary<ConfiglueStandardLocation, Func<string, string?>>());

    /// <summary>Creates a profile that uses <paramref name="fallback"/> for unmodified locations.</summary>
    public static ConfiglueHostPathProfile From(IConfiglueHostPaths fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return new ConfiglueHostPathProfile(
            fallback,
            new Dictionary<ConfiglueStandardLocation, Func<string, string?>>()
        );
    }

    /// <summary>Overrides one location; return null to explicitly mark that location unsupported.</summary>
    public ConfiglueHostPathProfile WithOverride(
        ConfiglueStandardLocation location,
        Func<string, string?> resolver
    )
    {
        if (!Enum.IsDefined(location))
        {
            throw new ArgumentOutOfRangeException(nameof(location));
        }
        ArgumentNullException.ThrowIfNull(resolver);
        var overrides = new Dictionary<ConfiglueStandardLocation, Func<string, string?>>(_overrides)
        {
            [location] = resolver,
        };
        return new ConfiglueHostPathProfile(this, overrides);
    }

    /// <inheritdoc />
    public bool TryResolve(
        ConfiglueStandardLocation location,
        string applicationId,
        out string directory
    )
    {
        if (_overrides.TryGetValue(location, out var resolver))
        {
            var resolved = resolver(applicationId);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                directory = resolved;
                return true;
            }

            directory = string.Empty;
            return false;
        }

        if (_fallback is not null)
        {
            return _fallback.TryResolve(location, applicationId, out directory);
        }

        return ConfiglueStandardPaths.TryResolveDefault(location, applicationId, out directory);
    }
}
