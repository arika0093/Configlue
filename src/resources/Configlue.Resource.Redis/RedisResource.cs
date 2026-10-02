using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Configlue.Internal;
using Configlue.Sources;
using StackExchange.Redis;

namespace Configlue.Resource.Redis;

/// <summary>Reads and writes subject-scoped byte resources stored in Redis.</summary>
/// <remarks>
/// Each row uses a provider-generated Redis key. Route resolvers choose shared, caller-owned
/// multiplexers for physical placement; Redis remains a persistent state resource, not a cache policy.
/// The route resolver is invoked for every operation and its caller-owned multiplexer remains
/// externally owned; only Configlue-owned backends are cached and disposed.
/// </remarks>
public sealed class RedisResource
    : IResourceReader,
        IResourceWriter,
        IContextualResourceIdentity,
        ISourceWatcher,
        IDisposable
{
    private readonly Func<RouteKey, object>? _connectionResolver;
    private readonly RedisResourceOptions _options;
    private readonly bool _routeAwareIdentity;
    private readonly ResidencyCache<object, IRedisStateBackend>? _backendCache;
    private readonly ResidencyCache<RouteKey, IRedisStateBackend>? _testBackendCache;
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
        _connectionResolver = route =>
            (object?)connectionMultiplexerResolver(route)
            ?? throw new InvalidOperationException(
                "The Redis connection multiplexer resolver returned null."
            );
        var backendFactory = (object connection) =>
            new RedisStateBackend((IConnectionMultiplexer)connection, _options);
        _routeAwareIdentity = routeAwareIdentity;
        ResourceNamespace = resourceNamespace;
        _backendCache = new ResidencyCache<object, IRedisStateBackend>(
            backendFactory,
            ReferenceComparer<object>.Instance,
            _options.BackendCacheIdleTimeout,
            _options.BackendCacheCapacity
        );
    }

    internal RedisResource(
        Func<RouteKey, object> connectionResolver,
        Func<object, IRedisStateBackend> backendFactory,
        string resourceNamespace,
        RedisResourceOptions? options
    )
    {
        ArgumentNullException.ThrowIfNull(connectionResolver);
        ArgumentNullException.ThrowIfNull(backendFactory);
        ValidateNamespace(resourceNamespace);
        _options = options ?? new RedisResourceOptions();
        _options.Validate();
        _connectionResolver = route =>
            connectionResolver(route)
            ?? throw new InvalidOperationException("The Redis connection resolver returned null.");
        _routeAwareIdentity = true;
        ResourceNamespace = resourceNamespace;
        _backendCache = new ResidencyCache<object, IRedisStateBackend>(
            backendFactory,
            ReferenceComparer<object>.Instance,
            _options.BackendCacheIdleTimeout,
            _options.BackendCacheCapacity
        );
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
        _routeAwareIdentity = true;
        ResourceNamespace = resourceNamespace;
        _testBackendCache = new ResidencyCache<RouteKey, IRedisStateBackend>(
            route =>
                backendResolver(route)
                ?? throw new InvalidOperationException(
                    "The Redis state backend resolver returned null."
                ),
            null,
            _options.BackendCacheIdleTimeout,
            _options.BackendCacheCapacity
        );
    }

    /// <summary>The namespace that separates this resource's Redis keys.</summary>
    public string ResourceNamespace { get; }

    /// <summary>The number of cached Configlue-owned backends, for diagnostics and tests.</summary>
    internal int CachedBackendCount => _testBackendCache?.Count ?? _backendCache?.Count ?? 0;

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
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        using var backend = AcquireBackend(context.Route);
        return await backend
            .Value.ReadAsync(ResolveAddress(context), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        using var backend = AcquireBackend(context.Route);
        return await backend
            .Value.WriteAsync(ResolveAddress(context), request, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        using var backend = AcquireBackend(context.Route);
        await backend
            .Value.WaitForChangeAsync(ResolveAddress(context), observedRevision, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _backendCache?.Dispose();
        _testBackendCache?.Dispose();
    }

    /// <summary>Forces an idle sweep and enforces the backend capacity bound, for tests.</summary>
    internal void TrimBackendCache()
    {
        _backendCache?.Trim();
        _testBackendCache?.Trim();
    }

    private BackendLease AcquireBackend(RouteKey route)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_testBackendCache is { } testCache)
        {
            var testLease = testCache.Acquire(route);
            return new BackendLease(testLease.Value, testLease);
        }

        var connection = _connectionResolver!(route);
        var backendLease = _backendCache!.Acquire(connection);
        return new BackendLease(backendLease.Value, backendLease);
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

    private readonly struct BackendLease : IDisposable
    {
        private readonly IDisposable _lease;

        public BackendLease(IRedisStateBackend value, IDisposable lease)
        {
            Value = value;
            _lease = lease;
        }

        public IRedisStateBackend Value { get; }

        public void Dispose() => _lease.Dispose();
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

internal sealed class ReferenceComparer<T> : IEqualityComparer<T>
    where T : class
{
    public static ReferenceComparer<T> Instance { get; } = new();

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
