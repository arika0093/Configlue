namespace Configlue.Resource.Etcd;

/// <summary>TLS and mTLS settings for etcd endpoints. Values are never included in diagnostic text.</summary>
public sealed class EtcdTlsOptions
{
    /// <summary>PEM-encoded certificate authority bundle used to verify etcd endpoints.</summary>
    public string? CaCertificatePem { get; init; }

    /// <summary>Path to a certificate authority bundle used to verify etcd endpoints.</summary>
    public string? CaCertificatePath { get; init; }

    /// <summary>PEM-encoded client certificate used for mTLS authentication.</summary>
    public string? ClientCertificatePem { get; init; }

    /// <summary>Path to a client certificate used for mTLS authentication.</summary>
    public string? ClientCertificatePath { get; init; }

    /// <summary>PEM-encoded client private key used for mTLS authentication.</summary>
    public string? ClientKeyPem { get; init; }

    /// <summary>Path to a client private key used for mTLS authentication.</summary>
    public string? ClientKeyPath { get; init; }

    /// <summary>Whether to skip verification of the etcd endpoint certificate chain.</summary>
    public bool InsecureSkipVerify { get; init; }

    /// <summary>An explicit TLS server-name override used for endpoint verification.</summary>
    public string? TargetNameOverride { get; init; }

    /// <summary>Returns a redacted description that never contains certificate material.</summary>
    public override string ToString() => "EtcdTlsOptions(redacted)";
}

/// <summary>Authentication material for etcd endpoints. Secrets are never included in diagnostic text.</summary>
public sealed class EtcdAuthOptions
{
    /// <summary>The etcd username used for password authentication.</summary>
    public string? Username { get; init; }

    /// <summary>Resolves the etcd password on demand so it is not retained in options.</summary>
    public Func<string>? PasswordProvider { get; init; }

    /// <summary>Resolves an etcd authentication token on demand.</summary>
    public Func<string>? TokenProvider { get; init; }

    /// <summary>Returns a redacted description that never contains credentials.</summary>
    public override string ToString() => "EtcdAuthOptions(redacted)";
}

/// <summary>Configures etcd key prefixes, endpoints, transport security, and watch recovery.</summary>
public sealed class EtcdResourceOptions
{
    /// <summary>The key prefix backing one model contribution.</summary>
    public string KeyPrefix { get; init; } = "configlue";

    /// <summary>Resolves the key prefix for one subject-aware resource operation.</summary>
    public Func<ConfiglueResourceContext, string>? KeyPrefixSelector { get; init; }

    /// <summary>The etcd endpoints used when a client factory dials a new connection.</summary>
    public IReadOnlyList<string> Endpoints { get; init; } = ["http://127.0.0.1:2379"];

    /// <summary>TLS and mTLS settings applied to endpoint connections.</summary>
    public EtcdTlsOptions? Tls { get; init; }

    /// <summary>Authentication material used for endpoint connections.</summary>
    public EtcdAuthOptions? Auth { get; init; }

    /// <summary>The delay between watch reconnect attempts after a transient failure.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The maximum watch reconnect attempts after a transient failure, or zero to retry
    /// until the caller cancels.
    /// </summary>
    public int MaxReconnectAttempts { get; init; }

    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all contexts routed through the resource share one physical coordination
    /// domain; an incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }

    internal TimeSpan BackendCacheIdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    internal int BackendCacheCapacity { get; init; } = 256;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(KeyPrefix);
        ValidatePrefix(KeyPrefix);
        ValidateEndpoints(Endpoints, nameof(Endpoints));
        ValidateDelay(ReconnectDelay, nameof(ReconnectDelay));
        ValidateAttempts(MaxReconnectAttempts, nameof(MaxReconnectAttempts));
    }

    internal static void ValidateEndpoints(IReadOnlyList<string>? endpoints, string parameterName)
    {
        if (endpoints is null || endpoints.Count == 0)
        {
            throw new ArgumentException(
                "The endpoints list requires at least one etcd endpoint.",
                parameterName
            );
        }

        if (endpoints.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "The endpoints list cannot contain empty values.",
                parameterName
            );
        }
    }

    internal static void ValidateDelay(TimeSpan delay, string parameterName)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The etcd reconnect delay cannot be negative."
            );
        }
    }

    internal static void ValidateAttempts(int attempts, string parameterName)
    {
        if (attempts < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The etcd reconnect attempt bound cannot be negative."
            );
        }
    }

    internal static void ValidatePrefix(string prefix)
    {
        if (prefix.Contains('\0'))
        {
            throw new ArgumentException("An etcd key prefix cannot contain NUL.", nameof(prefix));
        }
    }
}
