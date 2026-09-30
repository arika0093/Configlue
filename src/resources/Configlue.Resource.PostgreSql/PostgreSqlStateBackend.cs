#pragma warning disable S2077 // SQL identifiers are validated and quoted; values use command parameters.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;

namespace Configlue.Resource.PostgreSql;

internal sealed class PostgreSqlStateBackend : IPostgreSqlStateBackend
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlResourceOptions _options;
    private readonly PostgreSqlChangeHubLease _changeHub;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly string _qualifiedTable;
    private volatile bool _initialized;

    public PostgreSqlStateBackend(NpgsqlDataSource dataSource, PostgreSqlResourceOptions options)
    {
        _dataSource = dataSource;
        _options = options;
        _qualifiedTable = $"{Quote(options.SchemaName)}.{Quote(options.TableName)}";
        _changeHub = PostgreSqlChangeHubRegistry.Acquire(
            dataSource,
            "clue_" + PostgreSqlIdentityHash.Create(options.SchemaName, options.TableName)[..58]
        );
    }

    public async ValueTask<ResourceReadResult> ReadAsync(
        string resourceNamespace,
        string modelId,
        string subjectKey,
        CancellationToken cancellationToken
    )
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT "payload", "revision", "schema_version"
            FROM {_qualifiedTable}
            WHERE "model_id" = @model_id
                AND "resource_namespace" = @namespace
                AND "subject_key" = @subject_key
            """;
        AddText(command, "model_id", modelId);
        AddText(command, "namespace", resourceNamespace);
        AddText(command, "subject_key", subjectKey);

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ResourceReadResult.NotFound();
        }

        StateSchemaMetadata? schema = reader.IsDBNull(2)
            ? null
            : new StateSchemaMetadata(modelId, reader.GetInt32(2));
        return ResourceReadResult.Success(
            reader.GetFieldValue<byte[]>(0),
            reader.GetInt64(1).ToString(CultureInfo.InvariantCulture),
            schema
        );
    }

    public async ValueTask<StateWriteResult> WriteAsync(
        string resourceNamespace,
        string modelId,
        string subjectKey,
        ResourceWriteRequest request,
        CancellationToken cancellationToken
    )
    {
        ValidatePayloadModelId(modelId, request);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        string? revision;
        if (request.Condition.IsNone)
        {
            revision = await WriteUnconditionallyAsync(
                    connection,
                    transaction,
                    resourceNamespace,
                    modelId,
                    subjectKey,
                    request,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else if (request.Condition.IsMustNotExist)
        {
            revision = await WriteIfMissingAsync(
                    connection,
                    transaction,
                    resourceNamespace,
                    modelId,
                    subjectKey,
                    request,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else
        {
            revision = await WriteIfRevisionMatchesAsync(
                    connection,
                    transaction,
                    resourceNamespace,
                    modelId,
                    subjectKey,
                    request,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        if (revision is null)
        {
            throw new StateConflictException(
                $"The PostgreSQL resource '{resourceNamespace}/{modelId}/{subjectKey}' no longer matches its expected revision."
            );
        }

        await using (var notify = connection.CreateCommand())
        {
            notify.Transaction = transaction;
            notify.CommandText = "SELECT pg_notify(@channel, @identity)";
            AddText(notify, "channel", _changeHub.Hub.Channel);
            AddText(notify, "identity", HashIdentity(resourceNamespace, modelId, subjectKey));
            await notify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StateWriteResult(revision);
    }

    public ValueTask WaitForChangeAsync(
        string resourceNamespace,
        string modelId,
        string subjectKey,
        string? observedRevision,
        CancellationToken cancellationToken
    ) =>
        _changeHub.Hub.WaitForChangeAsync(
            HashIdentity(resourceNamespace, modelId, subjectKey),
            observedRevision,
            token => ReadAsync(resourceNamespace, modelId, subjectKey, token),
            cancellationToken
        );

    public void Dispose() => _changeHub.Dispose();

    internal static string HashIdentity(params string[] values) =>
        PostgreSqlIdentityHash.Create(values);

    private async ValueTask<string?> WriteUnconditionallyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string resourceNamespace,
        string modelId,
        string subjectKey,
        ResourceWriteRequest request,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {_qualifiedTable} AS target
                ("model_id", "resource_namespace", "subject_key", "payload", "revision", "schema_version", "updated_at")
            VALUES (@model_id, @namespace, @subject_key, @payload, 1, @schema_version, clock_timestamp())
            ON CONFLICT ("model_id", "resource_namespace", "subject_key") DO UPDATE SET
                "payload" = EXCLUDED."payload",
                "revision" = target."revision" + 1,
                "schema_version" = EXCLUDED."schema_version",
                "updated_at" = clock_timestamp()
            RETURNING "revision"::text
            """;
        AddWriteParameters(command, resourceNamespace, modelId, subjectKey, request);
        return await ExecuteRevisionAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string?> WriteIfMissingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string resourceNamespace,
        string modelId,
        string subjectKey,
        ResourceWriteRequest request,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {_qualifiedTable}
                ("model_id", "resource_namespace", "subject_key", "payload", "revision", "schema_version", "updated_at")
            VALUES (@model_id, @namespace, @subject_key, @payload, 1, @schema_version, clock_timestamp())
            ON CONFLICT ("model_id", "resource_namespace", "subject_key") DO NOTHING
            RETURNING "revision"::text
            """;
        AddWriteParameters(command, resourceNamespace, modelId, subjectKey, request);
        return await ExecuteRevisionAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string?> WriteIfRevisionMatchesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string resourceNamespace,
        string modelId,
        string subjectKey,
        ResourceWriteRequest request,
        CancellationToken cancellationToken
    )
    {
        if (
            !long.TryParse(
                request.Condition.Revision,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expectedRevision
            )
            || expectedRevision <= 0
        )
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {_qualifiedTable}
            SET "payload" = @payload,
                "revision" = "revision" + 1,
                "schema_version" = @schema_version,
                "updated_at" = clock_timestamp()
            WHERE "model_id" = @model_id
                AND "resource_namespace" = @namespace
                AND "subject_key" = @subject_key
                AND "revision" = @expected_revision
            RETURNING "revision"::text
            """;
        AddWriteParameters(command, resourceNamespace, modelId, subjectKey, request);
        command.Parameters.AddWithValue("expected_revision", NpgsqlDbType.Bigint, expectedRevision);
        return await ExecuteRevisionAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePayloadModelId(string modelId, ResourceWriteRequest request)
    {
        if (
            request.Schema is { ModelId: { } payloadModelId }
            && !string.Equals(payloadModelId, modelId, StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                $"The payload schema model ID '{payloadModelId}' does not match the resource context model ID '{modelId}'."
            );
        }
    }

    private static async ValueTask<string?> ExecuteRevisionAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken
    )
    {
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static void AddWriteParameters(
        NpgsqlCommand command,
        string resourceNamespace,
        string modelId,
        string subjectKey,
        ResourceWriteRequest request
    )
    {
        AddText(command, "model_id", modelId);
        AddText(command, "namespace", resourceNamespace);
        AddText(command, "subject_key", subjectKey);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, request.Content.ToArray());
        var schema = request.Schema;
        command.Parameters.AddWithValue(
            "schema_version",
            NpgsqlDbType.Integer,
            schema is { } metadata ? metadata.Version : DBNull.Value
        );
    }

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.AddWithValue(name, NpgsqlDbType.Text, value);

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await _dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE SCHEMA IF NOT EXISTS {Quote(_options.SchemaName)};
                CREATE TABLE IF NOT EXISTS {_qualifiedTable} (
                    "model_id" text NOT NULL,
                    "resource_namespace" text NOT NULL,
                    "subject_key" text NOT NULL,
                    "payload" bytea NOT NULL,
                    "revision" bigint NOT NULL CHECK ("revision" > 0),
                    "schema_version" integer NULL,
                    "updated_at" timestamptz NOT NULL,
                    PRIMARY KEY ("model_id", "resource_namespace", "subject_key")
                )
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';
}

internal sealed class PostgreSqlChangeHub : IDisposable
{
    private readonly PostgreSqlNotificationLoop _notificationLoop;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _initialConnection = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly ConcurrentDictionary<
        string,
        ConcurrentDictionary<long, TaskCompletionSource>
    > _waiters = new(StringComparer.Ordinal);
    private readonly object _startGate = new();
    private Task? _listenerTask;
    private long _nextWaiterId;
    private int _disposed;

    public PostgreSqlChangeHub(NpgsqlDataSource dataSource, string channel)
        : this(
            channel,
            (listenChannel, onNotification, onReconnect, onReady, cancellationToken) =>
                ListenAsync(
                    dataSource,
                    listenChannel,
                    onNotification,
                    onReconnect,
                    onReady,
                    cancellationToken
                )
        ) { }

    internal PostgreSqlChangeHub(string channel, PostgreSqlNotificationLoop notificationLoop)
    {
        _notificationLoop = notificationLoop;
        Channel = channel;
    }

    public string Channel { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public async ValueTask WaitForChangeAsync(
        string identity,
        string? observedRevision,
        Func<CancellationToken, ValueTask<ResourceReadResult>> readCurrent,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var identityWaiters = _waiters.GetOrAdd(
            identity,
            static _ => new ConcurrentDictionary<long, TaskCompletionSource>()
        );
        var waiterId = Interlocked.Increment(ref _nextWaiterId);
        identityWaiters[waiterId] = signal;
        try
        {
            EnsureListenerStarted();
            await _initialConnection.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            var current = await readCurrent(cancellationToken).ConfigureAwait(false);
            if (
                current.Status != StateReadStatus.Unavailable
                && !string.Equals(current.Revision, observedRevision, StringComparison.Ordinal)
            )
            {
                return;
            }

            await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            identityWaiters.TryRemove(waiterId, out _);
            if (identityWaiters.IsEmpty)
            {
                _waiters.TryRemove(
                    new KeyValuePair<string, ConcurrentDictionary<long, TaskCompletionSource>>(
                        identity,
                        identityWaiters
                    )
                );
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _initialConnection.TrySetResult();
        SignalAllWaiters();
        _shutdown.Cancel();
        lock (_startGate)
        {
            try
            {
                _listenerTask?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                _listenerTask = Task.CompletedTask;
            }
        }

        _shutdown.Dispose();
    }

    private void EnsureListenerStarted()
    {
        lock (_startGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _listenerTask ??= Task.Run(
                () =>
                    _notificationLoop(
                        Channel,
                        OnNotification,
                        SignalAllWaiters,
                        () => _initialConnection.TrySetResult(),
                        _shutdown.Token
                    ),
                _shutdown.Token
            );
        }
    }

    private static async Task ListenAsync(
        NpgsqlDataSource dataSource,
        string channel,
        Action<string, string> onNotification,
        Action onReconnect,
        Action onReady,
        CancellationToken cancellationToken
    )
    {
        var hasConnected = false;
        var retryDelay = TimeSpan.FromMilliseconds(200);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource
                    .OpenConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                connection.Notification += (_, args) => onNotification(args.Channel, args.Payload);
                await using (var listen = connection.CreateCommand())
                {
                    listen.CommandText = $"LISTEN \"{channel}\"";
                    await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                if (hasConnected)
                {
                    onReconnect();
                }

                hasConnected = true;
                retryDelay = TimeSpan.FromMilliseconds(200);
                onReady();
                while (!cancellationToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                retryDelay = TimeSpan.FromMilliseconds(
                    Math.Min(retryDelay.TotalMilliseconds * 2, 30_000)
                );
            }
        }
    }

    private void OnNotification(string channel, string payload)
    {
        if (
            string.Equals(channel, Channel, StringComparison.Ordinal)
            && _waiters.TryGetValue(payload, out var identityWaiters)
        )
        {
            foreach (var signal in identityWaiters.Values)
            {
                signal.TrySetResult();
            }
        }
    }

    private void SignalAllWaiters()
    {
        foreach (var identityWaiters in _waiters.Values)
        {
            foreach (var signal in identityWaiters.Values)
            {
                signal.TrySetResult();
            }
        }
    }
}

internal delegate Task PostgreSqlNotificationLoop(
    string channel,
    Action<string, string> onNotification,
    Action onReconnect,
    Action onReady,
    CancellationToken cancellationToken
);

internal static class PostgreSqlChangeHubRegistry
{
    private static readonly ConditionalWeakTable<NpgsqlDataSource, HubRegistry> Registries = new();

    public static PostgreSqlChangeHubLease Acquire(NpgsqlDataSource dataSource, string channel)
    {
        var registry = Registries.GetValue(dataSource, static _ => new HubRegistry());
        return registry.AcquireHub(dataSource, channel);
    }

    internal sealed class HubRegistry
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, HubEntry> _hubs = new(StringComparer.Ordinal);

        public PostgreSqlChangeHubLease AcquireHub(NpgsqlDataSource dataSource, string channel)
        {
            lock (_gate)
            {
                if (!_hubs.TryGetValue(channel, out var entry))
                {
                    entry = new HubEntry(new PostgreSqlChangeHub(dataSource, channel));
                    _hubs.Add(channel, entry);
                }

                entry.LeaseCount++;
                return new PostgreSqlChangeHubLease(this, channel, entry.Hub);
            }
        }

        public void Release(string channel)
        {
            PostgreSqlChangeHub? hubToDispose = null;
            lock (_gate)
            {
                if (_hubs.TryGetValue(channel, out var entry) && --entry.LeaseCount == 0)
                {
                    _hubs.Remove(channel);
                    hubToDispose = entry.Hub;
                }
            }

            hubToDispose?.Dispose();
        }
    }

    private sealed class HubEntry(PostgreSqlChangeHub hub)
    {
        public PostgreSqlChangeHub Hub { get; } = hub;

        public int LeaseCount { get; set; }
    }
}

internal sealed class PostgreSqlChangeHubLease : IDisposable
{
    private PostgreSqlChangeHubRegistry.HubRegistry? _registry;
    private readonly string _channel;

    internal PostgreSqlChangeHubLease(
        PostgreSqlChangeHubRegistry.HubRegistry registry,
        string channel,
        PostgreSqlChangeHub hub
    )
    {
        _registry = registry;
        _channel = channel;
        Hub = hub;
    }

    public PostgreSqlChangeHub Hub { get; }

    public void Dispose() => Interlocked.Exchange(ref _registry, null)?.Release(_channel);
}
