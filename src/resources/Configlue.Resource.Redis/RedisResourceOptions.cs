namespace Configlue.Resource.Redis;

/// <summary>Configures Redis keys, databases, notifications, and resource identity.</summary>
public sealed class RedisResourceOptions
{
    /// <summary>The prefix applied to provider-generated Redis keys.</summary>
    public string KeyPrefix { get; init; } = "configlue";

    /// <summary>Resolves a key prefix for one subject-aware resource operation.</summary>
    public Func<ConfiglueResourceContext, string>? KeyPrefixSelector { get; init; }

    /// <summary>The logical Redis database number, or -1 for the multiplexer default.</summary>
    public int Database { get; init; } = -1;

    /// <summary>Resolves the Redis database number for one resource operation.</summary>
    public Func<ConfiglueResourceContext, int>? DatabaseSelector { get; init; }

    /// <summary>The Pub/Sub channel used for Configlue row invalidations.</summary>
    public string NotificationChannel { get; init; } = "configlue:resource:changed";

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
        ArgumentException.ThrowIfNullOrWhiteSpace(NotificationChannel);
        ValidateDatabase(Database);
    }

    internal static void ValidateDatabase(int database)
    {
        if (database < -1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(database),
                "A Redis database number must be -1 (the multiplexer default) or greater."
            );
        }
    }
}
