using System.Text.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;

namespace Configlue.Resource.AzureAppConfiguration;

/// <summary>Options for an Azure App Configuration source.</summary>
/// <remarks>
/// <para>
/// Azure identity is the documented default: supply <see cref="Endpoint"/> with
/// <see cref="Credential"/> (for example <c>DefaultAzureCredential</c> from Azure.Identity)
/// or inject a caller-owned <see cref="Client"/>. Connection strings are allowed via
/// <see cref="ConnectionString"/> but are not the default.
/// </para>
/// <para>Credentials and secret-reference contents are never logged and never appear in resource identities.</para>
/// <para>
/// Key Vault references are preserved faithfully as opaque values and are never resolved here.
/// Compose with the Azure Key Vault provider when resolution is required.
/// </para>
/// <para>
/// One Configlue commit that updates several App Configuration keys is non-transactional:
/// keys are written sequentially and a mid-batch failure leaves earlier keys written.
/// Atomic multi-key semantics are not advertised.
/// </para>
/// </remarks>
public sealed class AzureAppConfigurationSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The App Configuration endpoint, for example <c>https://my-store.azconfig.io</c>.</summary>
    public Uri? Endpoint { get; init; }

    /// <summary>An Azure identity credential. The client remains caller-owned.</summary>
    public TokenCredential? Credential { get; init; }

    /// <summary>Resolves an Azure identity credential when the source context is created.</summary>
    public Func<IServiceProvider?, TokenCredential>? CredentialFactory { get; init; }

    /// <summary>A directly supplied client. It remains caller-owned and is never disposed.</summary>
    public ConfigurationClient? Client { get; init; }

    /// <summary>Resolves a caller-owned client at context creation, for example from dependency injection.</summary>
    public Func<IServiceProvider?, ConfigurationClient>? ClientFactory { get; init; }

    /// <summary>
    /// A connection string. Allowed for compatibility, but Azure identity is the documented default.
    /// The secret is never logged and never enters resource identities.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>The key filter; <c>*</c> selects all keys. Supports trailing-<c>*</c> and comma-separated filters.</summary>
    public string KeyFilter { get; init; } = "*";

    /// <summary>The label filter; null selects unlabeled entries.</summary>
    public string? LabelFilter { get; init; }

    /// <summary>An optional prefix stripped before mapping keys to member paths, like the .NET provider.</summary>
    public string? TrimKeyPrefix { get; init; }

    /// <summary>
    /// An explicit snapshot name used as a read mode. Snapshots are point-in-time and read-only:
    /// writes are rejected rather than pretending to be dynamic.
    /// </summary>
    public string? SnapshotName { get; init; }

    /// <summary>An optional sentinel key for coordinated multi-key publication.</summary>
    public string? SentinelKey { get; init; }

    /// <summary>The sentinel key's label; null means unlabeled.</summary>
    public string? SentinelLabel { get; init; }

    /// <summary>The minimum interval between refresh polls. The watcher is pull-based.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether this source exposes a writer. Writes are opt-in and default to read-only.</summary>
    public bool Writable { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>An advanced fixed identity override shared by every operation context.</summary>
    public ResourceId? FixedResourceId { get; init; }

    /// <summary>Optional scalar conversion override. Types it declines fall back to JSON.</summary>
    public Func<string, Type, object?>? ValueParser { get; init; }

    /// <summary>JSON options used for members without a scalar conversion, such as collections.</summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; init; }

    internal void Validate()
    {
        var mechanisms =
            (Client is null ? 0 : 1)
            + (ClientFactory is null ? 0 : 1)
            + (Endpoint is null ? 0 : 1)
            + (ConnectionString is null ? 0 : 1);
        if (mechanisms != 1)
        {
            throw new ArgumentException(
                "Configure exactly one of Client, ClientFactory, Endpoint, or ConnectionString."
            );
        }

        if (Endpoint is not null)
        {
            if ((Credential is null) == (CredentialFactory is null))
            {
                throw new ArgumentException(
                    "When Endpoint is configured, supply exactly one of Credential or CredentialFactory."
                );
            }
        }
        else if (Credential is not null || CredentialFactory is not null)
        {
            throw new ArgumentException("Credential and CredentialFactory require Endpoint.");
        }

        if (Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(KeyFilter);
        if (SnapshotName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(SnapshotName);
            if (Writable)
            {
                throw new ArgumentException(
                    "Snapshot mode is read-only; Writable must be false when SnapshotName is set."
                );
            }
        }

        if (SentinelKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(SentinelKey);
        }

        if (RefreshInterval < TimeSpan.Zero)
        {
            throw new ArgumentException("The refresh interval cannot be negative.");
        }

        if (TrimKeyPrefix is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(TrimKeyPrefix);
        }
    }

    internal AppConfigurationSelection ToSelection() => new(KeyFilter, LabelFilter, SnapshotName);
}
