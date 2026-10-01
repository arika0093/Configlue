using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Configlue.Sources;
using StackExchange.Redis;

namespace Configlue.Resource.Redis;

/// <summary>Reads and writes subject-scoped byte resources stored in Redis.</summary>
/// <remarks>
/// Each row uses a provider-generated Redis key. Route resolvers choose shared, caller-owned
/// multiplexers for physical placement; Redis remains a persistent state resource, not a cache policy.
/// </remarks>
public sealed class RedisResource
    : IResourceReader,
        IResourceWriter,
        IContextualResourceIdentity,
        ISourceWatcher,
        IDisposable
{
    private readonly Func<RouteKey, IConnectionMultiplexer>? _multiplexerResolver;
    private readonly Func<RouteKey, IRedisStateBackend>? _testBackendResolver;
    private readonly RedisResourceOptions _options;
    private readonly bool _routeAwareIdentity;
    private readonly ConcurrentDictionary<RouteKey, Lazy<IConnectionMultiplexer>> _multiplexers =
        new();
    private readonly ConcurrentDictionary<
        IConnectionMultiplexer,
        Lazy<IRedisStateBackend>
    > _backends = new(MultiplexerReferenceComparer.Instance);
    private readonly ConcurrentDictionary<RouteKey, Lazy<IRedisStateBackend>> _testBackends = new();
    private int _disposed;

    /// <summary>Creates a resource that uses one shared multiplexer.</summary>
    public RedisResource(
        IConnectionMultiplexer connectionMultiplexer,
        string resourceNamespace,
        RedisResourceOptions? options = null
    )
        : this(CreateFixedResolver(connectionMultiplexer), resourceNamespace, options, false) { }

    /// <summary>Creates a resource that resolves a shared multiplexer for each physical route.</summary>
    public RedisResource(
        Func<RouteKey, IConnectionMultiplexer> connectionMultiplexerResolver,
        string resourceNamespace,
        RedisResourceOptions? options = null
    )
        : this(connectionMultiplexerResolver, resourceNamespace, options, true) { }

    private RedisResource(
        Func<RouteKey, IConnectionMultiplexer> connectionMultiplexerResolver,
        string resourceNamespace,
        RedisResourceOptions? options,
        bool routeAwareIdentity
    )
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexerResolver);
        ValidateNamespace(resourceNamespace);
        _options = options ?? new RedisResourceOptions();
        _options.Validate();
        _multiplexerResolver = connectionMultiplexerResolver;
        _routeAwareIdentity = routeAwareIdentity;
        ResourceNamespace = resourceNamespace;
    }

    internal RedisResource(
        Func<RouteKey, IRedisStateBackend> backendResolver,
        string resourceNamespace,
        RedisResourceOptions? options
    )
    {
        ArgumentNullException.ThrowIfNull(backendResolver);
        ValidateNamespace(resourceNamespace);
        _options = options ?? new RedisResourceOptions();
        _options.Validate();
        _testBackendResolver = backendResolver;
        _routeAwareIdentity = true;
        ResourceNamespace = resourceNamespace;
    }

    /// <summary>The namespace that separates this resource's Redis keys.</summary>
    public string ResourceNamespace { get; }

    /// <inheritdoc />
    public ResourceId ResourceId => GetResourceId(ConfiglueResourceContext.Default);

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context)
    {
        if (_options.ResourceId is { } explicitId)
        {
            return explicitId;
        }

        var address = ResolveAddress(context);
        return new ResourceId(
            "redis:"
                + RedisIdentityHash.Create(
                    address.KeyPrefix,
                    ResourceNamespace,
                    context.ModelId ?? string.Empty,
                    context.Key.Value,
                    address.Database.ToString(CultureInfo.InvariantCulture),
                    _routeAwareIdentity ? context.Route.Value : string.Empty
                )
        );
    }

    /// <inheritdoc />
    public ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    ) => GetBackend(context.Route).ReadAsync(ResolveAddress(context), cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => GetBackend(context.Route).WriteAsync(ResolveAddress(context), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) =>
        GetBackend(context.Route)
            .WaitForChangeAsync(ResolveAddress(context), observedRevision, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var backend in _backends.Values)
        {
            if (backend.IsValueCreated)
            {
                backend.Value.Dispose();
            }
        }

        foreach (var backend in _testBackends.Values)
        {
            if (backend.IsValueCreated)
            {
                backend.Value.Dispose();
            }
        }
    }

    private RedisResourceAddress ResolveAddress(ConfiglueResourceContext context)
    {
        var prefix = _options.KeyPrefixSelector?.Invoke(context) ?? _options.KeyPrefix;
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var database = _options.DatabaseSelector?.Invoke(context) ?? _options.Database;
        RedisResourceOptions.ValidateDatabase(database);
        var modelId = context.ModelId ?? string.Empty;
        var rowIdentity = RedisIdentityHash.Create(ResourceNamespace, modelId, context.Key.Value);
        var redisKey = $"{prefix}:{rowIdentity}";
        var notificationIdentity = RedisIdentityHash.Create(
            prefix,
            ResourceNamespace,
            modelId,
            context.Key.Value,
            database.ToString(CultureInfo.InvariantCulture)
        );
        return new RedisResourceAddress(
            redisKey,
            prefix,
            database,
            _options.NotificationChannel,
            notificationIdentity
        );
    }

    private IRedisStateBackend GetBackend(RouteKey route)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_testBackendResolver is { } testResolver)
        {
            return _testBackends
                .GetOrAdd(
                    route,
                    key => new Lazy<IRedisStateBackend>(
                        () => testResolver(key),
                        LazyThreadSafetyMode.ExecutionAndPublication
                    )
                )
                .Value;
        }

        var multiplexer = _multiplexers
            .GetOrAdd(
                route,
                key => new Lazy<IConnectionMultiplexer>(
                    () =>
                        _multiplexerResolver!(key)
                        ?? throw new InvalidOperationException(
                            "The Redis connection multiplexer resolver returned null."
                        ),
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            )
            .Value;
        return _backends
            .GetOrAdd(
                multiplexer,
                connection => new Lazy<IRedisStateBackend>(
                    () => new RedisStateBackend(connection, _options),
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            )
            .Value;
    }

    private static Func<RouteKey, IConnectionMultiplexer> CreateFixedResolver(
        IConnectionMultiplexer connectionMultiplexer
    )
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        return _ => connectionMultiplexer;
    }

    private static void ValidateNamespace(string resourceNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceNamespace);
        if (resourceNamespace.Contains('\0'))
        {
            throw new ArgumentException(
                "A Redis string namespace cannot contain NUL.",
                nameof(resourceNamespace)
            );
        }
    }
}

internal readonly record struct RedisResourceAddress(
    string Key,
    string KeyPrefix,
    int Database,
    string NotificationChannel,
    string NotificationIdentity
);

internal interface IRedisStateBackend : IDisposable
{
    ValueTask<ResourceReadResult> ReadAsync(
        RedisResourceAddress address,
        CancellationToken cancellationToken
    );

    ValueTask<StateWriteResult> WriteAsync(
        RedisResourceAddress address,
        ResourceWriteRequest request,
        CancellationToken cancellationToken
    );

    ValueTask WaitForChangeAsync(
        RedisResourceAddress address,
        string? observedRevision,
        CancellationToken cancellationToken
    );
}

internal static class RedisIdentityHash
{
    public static string Create(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length.ToArray());
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

internal sealed class MultiplexerReferenceComparer : IEqualityComparer<IConnectionMultiplexer>
{
    public static MultiplexerReferenceComparer Instance { get; } = new();

    public bool Equals(IConnectionMultiplexer? left, IConnectionMultiplexer? right) =>
        ReferenceEquals(left, right);

    public int GetHashCode(IConnectionMultiplexer value) =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
}
