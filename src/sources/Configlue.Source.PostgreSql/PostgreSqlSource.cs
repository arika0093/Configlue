using System.Buffers;
using System.Collections.Concurrent;
using Configlue.Provider.Json;
using Configlue.Sources;
using Npgsql;

namespace Configlue.Source.PostgreSql;

/// <summary>A JSONB-native PostgreSQL source with read, optional write, and LISTEN/NOTIFY watch capabilities.</summary>
/// <remarks>
/// Rows are addressed by the model ID, resource namespace, and subject key, and store structured JSONB
/// payloads. This type performs DML only; create or upgrade the database schema with
/// Configlue.Source.PostgreSql.Migrations.
/// </remarks>
public sealed class PostgreSqlSource<T>
    : IContextualSourceReader<T>,
        IContextualSourceWriter<T>,
        IContextualSourceWatcher,
        ISourceCapabilities<T>,
        ITryContextualResourceIdentity,
        IDisposable
{
    private readonly Func<RouteKey, NpgsqlDataSource>? _dataSourceResolver;
    private readonly Func<RouteKey, IPostgreSqlStateBackend>? _testBackendResolver;
    private readonly PostgreSqlTableOptions _tableOptions;
    private readonly JsonStateValueSerializer<T> _serializer;
    private readonly bool _writable;
    private readonly bool _routeAwareIdentity;
    private readonly ConcurrentDictionary<RouteKey, Lazy<NpgsqlDataSource>> _dataSources = new();
    private readonly ConcurrentDictionary<
        NpgsqlDataSource,
        Lazy<IPostgreSqlStateBackend>
    > _backends = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<RouteKey, Lazy<IPostgreSqlStateBackend>> _testBackends =
        new();
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
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceNamespace);
        if (resourceNamespace.Contains('\0'))
        {
            throw new ArgumentException(
                "A PostgreSQL text namespace cannot contain NUL.",
                nameof(resourceNamespace)
            );
        }

        _tableOptions = tableOptions ?? new PostgreSqlTableOptions();
        _tableOptions.Validate();
        _dataSourceResolver = dataSourceResolver;
        _routeAwareIdentity = routeAwareIdentity;
        _serializer = serializer;
        _writable = writable;
        ResourceNamespace = resourceNamespace;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceNamespace);
        if (resourceNamespace.Contains('\0'))
        {
            throw new ArgumentException(
                "A PostgreSQL text namespace cannot contain NUL.",
                nameof(resourceNamespace)
            );
        }

        _tableOptions = tableOptions ?? new PostgreSqlTableOptions();
        _tableOptions.Validate();
        _testBackendResolver = backendResolver;
        _routeAwareIdentity = true;
        _serializer = serializer;
        _writable = writable;
        ResourceNamespace = resourceNamespace;
    }

    /// <summary>The namespace that separates this source's rows.</summary>
    public string ResourceNamespace { get; }

    /// <inheritdoc />
    public bool CanWrite => _writable;

    /// <inheritdoc />
    public ISourceWriter<T>? Writer => _writable ? this : null;

    /// <inheritdoc />
    public ISourceWatcher? Watcher => this;

    /// <inheritdoc />
    public ResourceId ResourceId => GetResourceId(ConfiglueResourceContext.Default);

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _tableOptions.ResourceId
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
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        resourceId = GetResourceId(context);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<StateReadResult<T>> ReadAsync(
        CancellationToken cancellationToken = default
    ) => await ReadAsync(ConfiglueResourceContext.Default, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<StateReadResult<T>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var result = await GetBackend(context.Route)
            .ReadAsync(
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
            ? StateReadResult<T>.Invalid(default, result.Revision) with
            {
                Schema = result.Schema,
            }
            : StateReadResult<T>.Success(value, result.Revision, result.Schema);
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    ) => WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
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
        return GetBackend(context.Route)
            .WriteAsync(
                ResourceNamespace,
                context.ModelId ?? string.Empty,
                context.Key.Value,
                resourceRequest,
                cancellationToken
            );
    }

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
                context.ModelId ?? string.Empty,
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
                    () => new PostgreSqlStateBackend(source, _tableOptions),
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            )
            .Value;
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
