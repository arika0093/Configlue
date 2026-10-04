namespace Configlue.Resource.GoogleSecretManager;

/// <summary>Options for a resource backed by one Google Cloud Secret Manager secret version.</summary>
/// <remarks>
/// <para>
/// Reads require <c>secretmanager.versions.access</c> on the secret (payload) and
/// <c>secretmanager.versions.get</c> for metadata-only polling. Adding versions requires
/// <c>secretmanager.versions.add</c> on the parent secret. Grant read-only callers only the
/// <c>Secret Manager Secret Accessor</c> subset and grant writers a role containing
/// <c>secretmanager.versions.add</c> separately.
/// </para>
/// <para>
/// The injected <see cref="IGoogleSecretManagerClient"/> remains caller-owned. Credential
/// material, access tokens, and secret payloads are never written to diagnostics or exceptions;
/// only resource names and numeric version IDs are safe for provenance and revision tracking.
/// </para>
/// </remarks>
public sealed class GoogleSecretManagerResourceOptions
{
    /// <summary>The Google Cloud location for regional secrets, or null for the default global secret.</summary>
    public string? Location { get; init; }

    /// <summary>
    /// The version selector: a fixed numeric version (immutable, for example <c>"3"</c>),
    /// <c>"latest"</c>, or a configured alias. Defaults to <c>"latest"</c>.
    /// </summary>
    public string Version { get; init; } = GoogleSecretManagerResource.LatestVersion;

    /// <summary>Resolves the project ID for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? ProjectIdSelector { get; init; }

    /// <summary>Resolves the secret ID for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? SecretIdSelector { get; init; }

    /// <summary>Resolves the location for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string?>? LocationSelector { get; init; }

    /// <summary>Resolves the version selector or alias for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? VersionSelector { get; init; }

    /// <summary>Resolves an externally owned Secret Manager client for each subject-aware operation.</summary>
    /// <remarks>Use a distinct route when the selected client points to a different project or credential.</remarks>
    public Func<ConfiglueResourceContext, IGoogleSecretManagerClient>? ClientSelector { get; init; }

    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all selected projects, locations, secrets, and versions share one physical
    /// coordination domain; an incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }

    /// <summary>
    /// The delay between lightweight metadata checks while watching a moving alias.
    /// This is the minimum interval between Secret Manager metadata calls. Choose at least
    /// 30 seconds in production to respect API quotas; shorter values exist for tests.
    /// Fixed numeric versions never poll regardless of this value.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether <see cref="GoogleSecretManagerResource.WriteAsync"/> may add new secret versions.
    /// Writes are opt-in and always append a new immutable version; aliases and secret metadata
    /// are never moved or mutated by writes.
    /// </summary>
    public bool EnableWrites { get; init; }

    /// <summary>
    /// Whether registration helpers expose this resource as a change watcher for moving aliases.
    /// Fixed numeric versions never create watchers regardless of this value.
    /// </summary>
    public bool EnableWatching { get; init; }

    internal void Validate()
    {
        if (Version is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Version);
        }

        if (Location is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Location);
        }

        if (PollInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The Secret Manager poll interval must be positive."
            );
        }
    }
}
