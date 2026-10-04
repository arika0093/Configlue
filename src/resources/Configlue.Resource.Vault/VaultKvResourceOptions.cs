namespace Configlue.Resource.Vault;

/// <summary>Options for a resource backed by one HashiCorp Vault KV secret.</summary>
public sealed class VaultKvResourceOptions
{
    /// <summary>The minimum polling interval for change observation. Defaults to five seconds.</summary>
    /// <remarks>
    /// Vault KV has no streaming watch, so <see cref="VaultKvResource.WaitForChangeAsync"/>
    /// polls secret metadata (v2) or content (v1). Smaller values detect changes faster but
    /// load the Vault server more; values below <see cref="MinimumPollingInterval"/> are rejected.
    /// </remarks>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The KV engine version of the addressed path.</summary>
    public VaultKvEngine KvVersion { get; init; } = VaultKvEngine.V2;

    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all selected mounts and paths share one physical coordination domain; an
    /// incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }

    /// <summary>Resolves the mount for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? MountSelector { get; init; }

    /// <summary>Resolves the secret path for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? PathSelector { get; init; }

    /// <summary>Resolves an externally owned Vault client for each subject-aware operation.</summary>
    /// <remarks>Use a distinct route when the selected client points to a different Vault cluster or namespace.</remarks>
    public Func<ConfiglueResourceContext, IVaultKvClient>? ClientSelector { get; init; }

    /// <summary>The smallest accepted <see cref="PollingInterval"/>.</summary>
    public static TimeSpan MinimumPollingInterval => TimeSpan.FromSeconds(1);

    internal static void Validate(VaultKvResourceOptions options, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(typeof(VaultKvEngine), options.KvVersion))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Unknown Vault KV engine version."
            );
        }

        if (options.PollingInterval < MinimumPollingInterval)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"A Vault polling interval must be at least {MinimumPollingInterval.TotalSeconds} second(s)."
            );
        }
    }
}
