using TUnit.Core;

namespace Configlue.Tests;

internal static class IntegrationEnvironment
{
    public static bool RedisEnabled => IsEnabled("CONFIGLUE_REDIS_INTEGRATION");

    public static string RedisConnectionString =>
        Value("CONFIGLUE_REDIS_CONNECTION", "localhost:6379");

    public static bool PostgreSqlEnabled => IsEnabled("CONFIGLUE_POSTGRES_INTEGRATION");

    public static string PostgreSqlConnectionString =>
        Value(
            "CONFIGLUE_POSTGRES_CONNECTION",
            "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=configlue"
        );

    public static bool S3Enabled => IsEnabled("CONFIGLUE_S3_INTEGRATION");

    public static string S3ServiceUrl => Value("CONFIGLUE_S3_SERVICE_URL", "http://localhost:4566");

    public static string S3AccessKey => Value("CONFIGLUE_S3_ACCESS_KEY", "test");

    public static string S3SecretKey => Value("CONFIGLUE_S3_SECRET_KEY", "test");

    public static string S3Region => Value("CONFIGLUE_S3_REGION", "us-east-1");

    private static bool IsEnabled(string name) =>
        string.Equals(
            Environment.GetEnvironmentVariable(name),
            "1",
            StringComparison.Ordinal
        );

    private static string Value(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}

/// <summary>Skips tests that require a real Redis server unless the integration lane enables them.</summary>
public sealed class RedisIntegrationAttribute : SkipAttribute
{
    public RedisIntegrationAttribute()
        : base(
            "Set CONFIGLUE_REDIS_INTEGRATION=1 and CONFIGLUE_REDIS_CONNECTION to run Redis integration tests."
        ) { }

    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!IntegrationEnvironment.RedisEnabled);
}

/// <summary>Skips tests that require a real PostgreSQL server unless the integration lane enables them.</summary>
public sealed class PostgreSqlIntegrationAttribute : SkipAttribute
{
    public PostgreSqlIntegrationAttribute()
        : base(
            "Set CONFIGLUE_POSTGRES_INTEGRATION=1 and CONFIGLUE_POSTGRES_CONNECTION to run PostgreSQL integration tests."
        ) { }

    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!IntegrationEnvironment.PostgreSqlEnabled);
}

/// <summary>Skips tests that require an S3-compatible service unless the integration lane enables them.</summary>
public sealed class S3IntegrationAttribute : SkipAttribute
{
    public S3IntegrationAttribute()
        : base(
            "Set CONFIGLUE_S3_INTEGRATION=1 and CONFIGLUE_S3_SERVICE_URL to run S3 integration tests."
        ) { }

    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!IntegrationEnvironment.S3Enabled);
}
