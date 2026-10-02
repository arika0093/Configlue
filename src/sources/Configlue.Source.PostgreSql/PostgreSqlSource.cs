using System.Buffers;
using Configlue.Internal;
using Configlue.Provider.Json;
using Configlue.Sources;
using Npgsql;

namespace Configlue.Source.PostgreSql;

/// <summary>A JSONB-native PostgreSQL source with read, optional write, and LISTEN/NOTIFY watch capabilities.</summary>
/// <remarks>
/// Rows are addressed by the model ID, resource namespace, and subject key, and store structured JSONB
/// payloads. This type performs DML only; create or upgrade the database schema with
/// Configlue.Source.PostgreSql.Migrations. The route resolver is invoked for every operation and its
/// caller-owned data source remains externally owned; only Configlue-owned backends are cached and disposed.
/// </remarks>
public sealed class PostgreSqlSource<T>
    : ISourceWriter<T>,
        ISourceWatcher,
        ISourceCapabilities<T>,
        IResourceIdentity,
        IDisposable
{
    private readonly Func<RouteKey, object>? _connectionResolver;
    private readonly PostgreSqlTableOptions _tableOptions;
    private readonly JsonStateValueSerializer<T> _serializer;
    private readonly bool _writable;
    private readonly bool _routeAwareIdentity;
    private readonly ResidencyCache<object, IPostgreSqlStateBackend>? _backendCache;
    private readonly ResidencyCache<RouteKey, IPostgreSqlStateBackend>? _testBackendCache;
    private readonly WatchShutdown _watchShutdown = new();
    private int _disposed;

    /// <summary>Creates a source whose subject operations use one shared data source.</summary>
    public PostgreSqlSource(
        NpgsqlDataSource dataSource,
        string resourceNamespace,
        JsonStateValueSerializer<T> serializer,
        PostgreSqlTableOptions? tableOptions = null,
        bool writable = true
    )
        : this(
            CreateDataSourceResolver(dataSource),
            resourceNamespace,
            serializer,
            tableOptions,
            writable,
            routeAwareIdentity: false
        ) { }

    /// <summary>Creates a source that resolves a shared data source for each physical route.</summary>
    public PostgreSqlSource(
        Func<RouteKey, NpgsqlDataSource> dataSourceResolver,
        string resourceNamespace,
        JsonStateValueSerializer<T> serializer,
        PostgreSqlTableOptions? tableOptions = null,
        bool writable = true
    )
        : this(
            dataSourceResolver,
            resourceNamespace,
            serializer,
            tableOptions,
            writable,
            routeAwareIdentity: true
        ) { }

    private PostgreSqlSource(
        Func<RouteKey, NpgsqlDataSource> dataSourceResolver,
        string resourceNamespace,
        JsonStateValueSerializer<T> serializer,
        PostgreSqlTableOptions? tableOptions,
        bool writable,
        bool routeAwareIdentity
    )
    {
        ArgumentNullException.ThrowIfNull(dataSourceResolver);
        ArgumentNullException.ThrowIfNull(serializer);
        ValidateNamespace(resourceNamespace);
        _tableOptions = tableOptions ?? new PostgreSqlTableOptions();
        _tableOptions.Validate();
        _connectionResolver = route =>
            (object?)dataSourceResolver(route)
            ?? throw new InvalidOperationException(
                "The PostgreSQL data source resolver returned null."
            );
        var backendFactory = (object connection) =>
            new PostgreSqlStateBackend((NpgsqlDataSource)connection, _tableOptions);
        _routeAwareIdentity = routeAwareIdentity;
        _serializer = serializer;
        _writable = writable;
        ResourceNamespace = resourceNamespace;
        _backendCache = new ResidencyCache<object, IPostgreSqlStateBackend>(
            backendFactory,
            ReferenceEqualityComparer.Instance,
            _tableOptions.BackendCacheIdleTimeout,
            _tableOptions.BackendCacheCapacity
        );
    }

    internal PostgreSqlSource(
        Func<RouteKey, object> connectionResolver,
        Func<object, IPostgreSqlStateBackend> backendFactory,
        string resourceNamespace,
        JsonStateValueSerializer<T> serializer,
        PostgreSqlTableOptions? tableOptions = null,
        bool writable = true
    )
    {
        ArgumentNullException.ThrowIfNull(connectionResolver);
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(serializer);
        ValidateNamespace(resourceNamespace);
        _tableOptions = tableOptions ?? new PostgreSqlTableOptions();
        _tableOptions.Validate();
        _connectionResolver = route =>
            connectionResolver(route)
            ?? throw new InvalidOperationException(
                "The PostgreSQL connection resolver returned null."
            );
        _routeAwareIdentity = true;
        _serializer = serializer;
        _writable = writable;
        ResourceNamespace = resourceNamespace;
        _backendCache = new ResidencyCache<object, IPostgreSqlStateBackend>(
            backendFactory,
            ReferenceEqualityComparer.Instance,
            _tableOptions.BackendCacheIdleTimeout,
            _tableOptions.BackendCacheCapacity
        );
    }

    internal PostgreSqlSource(
        Func<RouteKey, IPostgreSqlStateBackend> backendResolver,
        string resourceNamespace,
        JsonStateValueSerializer<T> serializer,
        PostgreSqlTableOptions? tableOptions = null,
        bool writable = true
    )
    {
        ArgumentNullException.ThrowIfNull(backendResolver);
        ArgumentNullException.ThrowIfNull(serializer);
        ValidateNamespace(resourceNamespace);
        _tableOptions = tableOptions ?? new PostgreSqlTableOptions();
        _tableOptions.Validate();
        _routeAwareIdentity = true;
        _serializer = serializer;
        _writable = writable;
        ResourceNamespace = resourceNamespace;
        _testBackendCache = new ResidencyCache<RouteKey, IPostgreSqlStateBackend>(
            route =>
                backendResolver(route)
                ?? throw new InvalidOperationException(
                    "The PostgreSQL state backend resolver returned null."
                ),
            null,
            _tableOptions.BackendCacheIdleTimeout,
            _tableOptions.BackendCacheCapacity
        );
    }

    /// <summary>The namespace that separates this source's rows.</summary>
    public string ResourceNamespace { get; }

    /// <summary>The number of cached Configlue-owned backends, for diagnostics and tests.</summary>
    internal int CachedBackendCount => _testBackendCache?.Count ?? _backendCache?.Count ?? 0;

    /// <inheritdoc />
    public bool CanWrite => _writable;

    /// <inheritdoc />
    public ISourceWriter<T>? Writer => _writable ? this : null;

    /// <inheritdoc />
    public ISourceWatcher? Watcher => this;

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _tableOptions.FixedResourceId
        ?? new ResourceId(
            "postgresql:"
                + PostgreSqlIdentityHash.Create(
                    _tableOptions.SchemaName,
                    _tableOptions.TableName,
                    ResourceNamespace,
                    context.ModelId ?? string.Empty,
                    context.Key.Value,
                    _routeAwareIdentity ? context.Route.Value : string.Empty
                )
        );

    /// <inheritdoc />
    public async ValueTask<StateReadResult<T>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        using var backend = AcquireBackend(context.Route);
        var result = await backend
            .Value.ReadAsync(
                ResourceNamespace,
                context.ModelId ?? string.Empty,
                context.Key.Value,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (result.Status != StateReadStatus.Success)
        {
            return result.Status == StateReadStatus.NotFound
                ? StateReadResult<T>.NotFound(result.Revision) with
                {
                    Schema = result.Schema,
                }
                : StateReadResult<T>.Unavailable(result.Revision) with
                {
                    Schema = result.Schema,
                };
        }

        var value = _serializer.Deserialize(new ReadOnlySequence<byte>(result.Content));
        return value is null
            ? StateReadResult<T>.InvalidPayload(default, result.Revision) with
            {
                Schema = result.Schema,
            }
            : StateReadResult<T>.Success(value, result.Revision, result.Schema);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        if (!_writable)
        {
            throw new InvalidOperationException(
                $"The PostgreSQL source '{ResourceNamespace}' is read-only."
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        var schema = request.Value is IConfiglueFragment fragment
            ? (StateSchemaMetadata?)fragment.Schema.ToMetadata()
            : null;
        ValidateSchemaModelId(context, schema);
        var buffer = new ArrayBufferWriter<byte>();
        _serializer.Serialize(request.Value, buffer);
        var resourceRequest = new ResourceWriteRequest(
            buffer.WrittenMemory,
            request.Condition,
            schema
        );
        using var backend = AcquireBackend(context.Route);
        return await backend
            .Value.WriteAsync(
                ResourceNamespace,
                context.ModelId ?? string.Empty,
                context.Key.Value,
                resourceRequest,
                cancellationToken
            )
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
        await _watchShutdown
            .WaitAsync(
                watchCancellationToken =>
                    backend.Value.WaitForChangeAsync(
                        ResourceNamespace,
                        context.ModelId ?? string.Empty,
                        context.Key.Value,
                        observedRevision,
                        watchCancellationToken
                    ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watchShutdown.Signal();
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

    private static void ValidateSchemaModelId(
        ConfiglueResourceContext context,
        StateSchemaMetadata? schema
    )
    {
        if (
            schema is { ModelId: { } payloadModelId }
            && !string.Equals(payloadModelId, context.ModelId, StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                $"The payload schema model ID '{payloadModelId}' does not match the source context model ID '{context.ModelId}'."
            );
        }
    }

    private static void ValidateNamespace(string resourceNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceNamespace);
        if (resourceNamespace.Contains('\0'))
        {
            throw new ArgumentException(
                "A PostgreSQL text namespace cannot contain NUL.",
                nameof(resourceNamespace)
            );
        }
    }

    private static Func<RouteKey, NpgsqlDataSource> CreateDataSourceResolver(
        NpgsqlDataSource dataSource
    )
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return _ => dataSource;
    }

    private readonly struct BackendLease : IDisposable
    {
        private readonly IDisposable _lease;

        public BackendLease(IPostgreSqlStateBackend value, IDisposable lease)
        {
            Value = value;
            _lease = lease;
        }

        public IPostgreSqlStateBackend Value { get; }

        public void Dispose() => _lease.Dispose();
    }
}

internal interface IPostgreSqlStateBackend : IDisposable
{
    ValueTask<ResourceReadResult> ReadAsync(
        string resourceNamespace,
        string modelId,
        string subjectKey,
        CancellationToken cancellationToken
    );

    ValueTask<StateWriteResult> WriteAsync(
        string resourceNamespace,
        string modelId,
        string subjectKey,
        ResourceWriteRequest request,
        CancellationToken cancellationToken
    );

    ValueTask WaitForChangeAsync(
        string resourceNamespace,
        string modelId,
        string subjectKey,
        string? observedRevision,
        CancellationToken cancellationToken
    );
}

internal static class PostgreSqlIdentityHash
{
    public static string Create(params string[] values)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256
        );
        Span<byte> lengthBuffer = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, bytes.Length);
            hash.AppendData(lengthBuffer);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
