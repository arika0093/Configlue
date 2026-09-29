using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Configlue.Resource.PostgreSql;

/// <summary>Reads and writes subject-scoped byte resources stored in PostgreSQL.</summary>
/// <remarks>
/// A row is addressed by resource namespace and subject key. Route selectors choose a shared
/// caller-owned <see cref="NpgsqlDataSource"/> for each physical placement route.
/// </remarks>
public sealed class PostgreSqlResource
    : IResourceReader,
        IResourceWriter,
        IResourceIdentity,
        IStateWatcher,
        IDisposable
{
    private readonly Func<RouteKey, NpgsqlDataSource>? _dataSourceResolver;
    private readonly Func<RouteKey, IPostgreSqlStateBackend>? _testBackendResolver;
    private readonly PostgreSqlResourceOptions _options;
    private readonly bool _routeAwareIdentity;
    private readonly ConcurrentDictionary<RouteKey, Lazy<NpgsqlDataSource>> _dataSources = new();
    private readonly ConcurrentDictionary<
        NpgsqlDataSource,
        Lazy<IPostgreSqlStateBackend>
    > _backends = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<RouteKey, Lazy<IPostgreSqlStateBackend>> _testBackends =
        new();
    private int _disposed;

    /// <summary>Creates a resource whose subject operations use one shared data source.</summary>
    public PostgreSqlResource(
        NpgsqlDataSource dataSource,
        string resourceNamespace,
        PostgreSqlResourceOptions? options = null
    )
        : this(CreateDataSourceResolver(dataSource), resourceNamespace, options, false) { }

    /// <summary>Creates a resource that resolves a shared data source for each physical route.</summary>
    public PostgreSqlResource(
        Func<RouteKey, NpgsqlDataSource> dataSourceResolver,
        string resourceNamespace,
        PostgreSqlResourceOptions? options = null
    )
        : this(dataSourceResolver, resourceNamespace, options, true) { }

    private PostgreSqlResource(
        Func<RouteKey, NpgsqlDataSource> dataSourceResolver,
        string resourceNamespace,
        PostgreSqlResourceOptions? options,
        bool routeAwareIdentity
    )
    {
        ArgumentNullException.ThrowIfNull(dataSourceResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceNamespace);
        if (resourceNamespace.Contains('\0'))
        {
            throw new ArgumentException(
                "A PostgreSQL text namespace cannot contain NUL.",
                nameof(resourceNamespace)
            );
        }

        _options = options ?? new PostgreSqlResourceOptions();
        _options.Validate();
        _dataSourceResolver = dataSourceResolver;
        _routeAwareIdentity = routeAwareIdentity;
        ResourceNamespace = resourceNamespace;
    }

    internal PostgreSqlResource(
        Func<RouteKey, IPostgreSqlStateBackend> backendResolver,
        string resourceNamespace,
        PostgreSqlResourceOptions? options
    )
    {
        ArgumentNullException.ThrowIfNull(backendResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceNamespace);
        if (resourceNamespace.Contains('\0'))
        {
            throw new ArgumentException(
                "A PostgreSQL text namespace cannot contain NUL.",
                nameof(resourceNamespace)
            );
        }

        _options = options ?? new PostgreSqlResourceOptions();
        _options.Validate();
        _testBackendResolver = backendResolver;
        _routeAwareIdentity = true;
        ResourceNamespace = resourceNamespace;
    }

    /// <summary>The namespace that separates this resource's rows.</summary>
    public string ResourceNamespace { get; }

    /// <inheritdoc />
    public ResourceId ResourceId => GetResourceId(ConfiglueResourceContext.Default);

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _options.ResourceId
        ?? new ResourceId(
            "postgresql:"
                + PostgreSqlStateBackend.HashIdentity(
                    _options.SchemaName,
                    _options.TableName,
                    ResourceNamespace,
                    context.Key.Value,
                    _routeAwareIdentity ? context.Route.Value : string.Empty
                )
        );

    /// <inheritdoc />
    public ValueTask<ResourceReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(ConfiglueResourceContext.Default, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    ) =>
        GetBackend(context.Route)
            .ReadAsync(ResourceNamespace, context.Key.Value, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) =>
        GetBackend(context.Route)
            .WriteAsync(ResourceNamespace, context.Key.Value, request, cancellationToken);

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) => WaitForChangeAsync(ConfiglueResourceContext.Default, observedRevision, cancellationToken);

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) =>
        GetBackend(context.Route)
            .WaitForChangeAsync(
                ResourceNamespace,
                context.Key.Value,
                observedRevision,
                cancellationToken
            );

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

    private IPostgreSqlStateBackend GetBackend(RouteKey route)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_testBackendResolver is { } testResolver)
        {
            return _testBackends
                .GetOrAdd(
                    route,
                    key => new Lazy<IPostgreSqlStateBackend>(
                        () => testResolver(key),
                        LazyThreadSafetyMode.ExecutionAndPublication
                    )
                )
                .Value;
        }

        var dataSource = _dataSources
            .GetOrAdd(
                route,
                key => new Lazy<NpgsqlDataSource>(
                    () =>
                        _dataSourceResolver!(key)
                        ?? throw new InvalidOperationException(
                            "The PostgreSQL data source resolver returned null."
                        ),
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            )
            .Value;
        return _backends
            .GetOrAdd(
                dataSource,
                source => new Lazy<IPostgreSqlStateBackend>(
                    () => new PostgreSqlStateBackend(source, _options),
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            )
            .Value;
    }

    private static Func<RouteKey, NpgsqlDataSource> CreateDataSourceResolver(
        NpgsqlDataSource dataSource
    )
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return _ => dataSource;
    }
}

internal interface IPostgreSqlStateBackend : IDisposable
{
    ValueTask<ResourceReadResult> ReadAsync(
        string resourceNamespace,
        string subjectKey,
        CancellationToken cancellationToken
    );

    ValueTask<StateWriteResult> WriteAsync(
        string resourceNamespace,
        string subjectKey,
        ResourceWriteRequest request,
        CancellationToken cancellationToken
    );

    ValueTask WaitForChangeAsync(
        string resourceNamespace,
        string subjectKey,
        string? observedRevision,
        CancellationToken cancellationToken
    );
}

internal static class PostgreSqlIdentityHash
{
    public static string Create(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBuffer = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, bytes.Length);
            hash.AppendData(lengthBuffer);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
