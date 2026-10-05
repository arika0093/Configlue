namespace Configlue.Sources;

/// <summary>Maps one model member path to one key in a keyed secret store.</summary>
/// <remarks>Property paths are canonical dotted member paths (for example <c>Database.Host</c>).</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed record KeyedSecretMapping
{
    /// <summary>Creates a mapping.</summary>
    public KeyedSecretMapping(string propertyPath, string key, string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (version is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }

        PropertyPath = propertyPath;
        Key = key;
        Version = version;
    }

    /// <summary>The canonical dotted member path.</summary>
    public string PropertyPath { get; init; }

    /// <summary>The secret store key.</summary>
    public string Key { get; init; }

    /// <summary>A fixed secret version for this member, or null for the current version.</summary>
    public string? Version { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"KeyedSecretMapping(path={PropertyPath}, key={Key}, version={Version ?? "<current>"})";
}

/// <summary>A fetched secret value with safe version metadata. Never appears in diagnostics.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed record KeyedSecretValue
{
    /// <summary>Creates a fetched secret value.</summary>
    public KeyedSecretValue(string value, string? version, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        Version = version;
        Enabled = enabled;
    }

    /// <summary>The secret value. Never surface this through diagnostics.</summary>
    public string Value { get; init; }

    /// <summary>The secret version identifier, when known.</summary>
    public string? Version { get; init; }

    /// <summary>Whether the secret version is enabled. Disabled secrets read as missing.</summary>
    public bool Enabled { get; init; }

    /// <inheritdoc />
    public override string ToString() => "KeyedSecretValue(value=REDACTED)";
}

/// <summary>Transport over a keyed secret store. Implementations must never include secret values in messages.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IKeyedSecretClient
{
    /// <summary>Gets one secret value. Returns null when the key or version is missing.</summary>
    /// <param name="key">The store key.</param>
    /// <param name="version">A fixed version, or null for the current version.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask<KeyedSecretValue?> GetAsync(
        string key,
        string? version,
        CancellationToken cancellationToken = default
    );

    /// <summary>Creates or updates one secret value. Returns the new version, when known.</summary>
    /// <param name="key">The store key.</param>
    /// <param name="value">The new secret value. Never captured in diagnostics.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask<string?> SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default
    );
}

/// <summary>The secret store is throttled or transiently unavailable. Never carries secret values.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class KeyedSecretUnavailableException : Exception
{
    /// <summary>Creates a redacted transient error.</summary>
    public KeyedSecretUnavailableException(string message)
        : base(message) { }

    /// <summary>Creates a redacted transient error.</summary>
    public KeyedSecretUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Options for a generic keyed-secret source shared by secret store providers.</summary>
/// <remarks>Diagnostics never include secret values; only keys, versions, and member paths.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class KeyedSecretSourceOptions
{
    /// <summary>
    /// When true, leaf members without an explicit mapping use the deterministic convention
    /// key (<c>string.Join(separator, propertyPath.Split('.'))</c> with an optional
    /// <c>prefix + separator</c> prepended). Explicit mappings always win.
    /// </summary>
    public bool EnableConventionMapping { get; init; }

    /// <summary>Optional prefix prepended to convention-generated keys.</summary>
    public string? ConventionPrefix { get; init; }

    /// <summary>Separator joining dotted member path segments into convention keys.</summary>
    public string ConventionSeparator { get; init; } = "-";

    /// <summary>Validates explicit and convention-generated keys, throwing when illegal.</summary>
    public Func<string, string>? KeyValidator { get; init; }

    /// <summary>A fixed secret version applied to mappings without their own version. Null selects the current version.</summary>
    public string? DefaultVersion { get; init; }

    /// <summary>Whether this source exposes a writer. Writes are opt-in and default to false.</summary>
    public bool Writable { get; init; }

    /// <summary>Optional polling interval for current-version changes. Null disables watching.</summary>
    public TimeSpan? PollInterval { get; init; }

    /// <summary>Optional scalar conversion override. Types it declines fall back to JSON.</summary>
    public Func<string, Type, object?>? ValueParser { get; init; }

    /// <summary>JSON options used for members without a scalar conversion, such as collections.</summary>
    public System.Text.Json.JsonSerializerOptions? JsonSerializerOptions { get; init; }

    /// <summary>Redacted physical origin reported on reads (no secret material).</summary>
    public string? PhysicalOrigin { get; init; }
}
