#pragma warning disable S2077 // SQL identifiers are validated and quoted; values use command parameters.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Configlue.Source.PostgreSql;

internal sealed class PostgreSqlStateBackend : IPostgreSqlStateBackend
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlChangeHubLease _changeHub;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly string _qualifiedTable;
    private readonly string _qualifiedComponentsTable;
    private volatile bool _verified;

    public PostgreSqlStateBackend(NpgsqlDataSource dataSource, PostgreSqlTableOptions options)
    {
        _dataSource = dataSource;
        _qualifiedTable =
            $"{PostgreSqlTableOptions.Quote(options.SchemaName)}.{PostgreSqlTableOptions.Quote(options.TableName)}";
        _qualifiedComponentsTable =
            $"{PostgreSqlTableOptions.Quote(options.SchemaName)}.{PostgreSqlTableOptions.Quote(options.ComponentsTableName)}";
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
        await EnsureCompatibleAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT "payload"::text, "revision", "schema_version"
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

        var payload = Encoding.UTF8.GetBytes(reader.GetString(0));
        var schema = new StateSchemaMetadata(modelId, reader.GetInt32(2));
        return ResourceReadResult.Success(
            payload,
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
        await EnsureCompatibleAsync(cancellationToken).ConfigureAwait(false);
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
                $"The PostgreSQL row '{resourceNamespace}/{modelId}/{subjectKey}' no longer matches its expected revision."
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
                $"The payload schema model ID '{payloadModelId}' does not match the source context model ID '{modelId}'."
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
        command.Parameters.AddWithValue(
            "payload",
            NpgsqlDbType.Jsonb,
            Encoding.UTF8.GetString(request.Content.Span)
        );
        var schema = request.Schema;
        command.Parameters.AddWithValue(
            "schema_version",
            NpgsqlDbType.Integer,
            schema is { } metadata ? metadata.Version : StateSchemaMetadata.InitialVersion
        );
    }

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.AddWithValue(name, NpgsqlDbType.Text, value);

    private async ValueTask EnsureCompatibleAsync(CancellationToken cancellationToken)
    {
        if (_verified)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_verified)
            {
                return;
            }

            await using var connection = await _dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            bool hasStateTable;
            bool hasComponentsTable;
            await using (var probe = connection.CreateCommand())
            {
                probe.CommandText =
                    "SELECT to_regclass(@state_table)::text, to_regclass(@components_table)::text";
                AddText(probe, "state_table", _qualifiedTable);
                AddText(probe, "components_table", _qualifiedComponentsTable);
                await using var reader = await probe
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw MissingSchema(_qualifiedTable, _qualifiedComponentsTable);
                }

                hasStateTable = !reader.IsDBNull(0);
                hasComponentsTable = !reader.IsDBNull(1);
            }

            int? componentVersion = null;
            if (hasStateTable && hasComponentsTable)
            {
                await using var versionCommand = connection.CreateCommand();
                versionCommand.CommandText =
                    $"SELECT \"version\" FROM {_qualifiedComponentsTable} WHERE \"component\" = @component";
                AddText(versionCommand, "component", PostgreSqlSchemaVersions.CoreComponent);
                var value = await versionCommand
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (value is not null and not DBNull)
                {
                    componentVersion = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
            }

            EnsureSchemaCompatible(
                hasStateTable,
                hasComponentsTable,
                componentVersion,
                _qualifiedTable,
                _qualifiedComponentsTable
            );
            _verified = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    internal static void EnsureSchemaCompatible(
        bool hasStateTable,
        bool hasComponentsTable,
        int? componentVersion,
        string qualifiedTable,
        string qualifiedComponentsTable
    )
    {
        if (!hasStateTable || !hasComponentsTable)
        {
            throw MissingSchema(qualifiedTable, qualifiedComponentsTable);
        }

        if (componentVersion is null)
        {
            throw new PostgreSqlSchemaException(
                $"The PostgreSQL schema component '{PostgreSqlSchemaVersions.CoreComponent}' is not registered. "
                    + "Apply the database schema with Configlue.Source.PostgreSql.Migrations before using the source."
            );
        }

        if (componentVersion < PostgreSqlSchemaVersions.CoreVersion)
        {
            throw new PostgreSqlSchemaException(
                $"The PostgreSQL schema component '{PostgreSqlSchemaVersions.CoreComponent}' has version {componentVersion}, "
                    + $"but version {PostgreSqlSchemaVersions.CoreVersion} or later is required. "
                    + "Upgrade the database schema with Configlue.Source.PostgreSql.Migrations."
            );
        }
    }

    private static PostgreSqlSchemaException MissingSchema(
        string qualifiedTable,
        string qualifiedComponentsTable
    ) =>
        new(
            $"The PostgreSQL schema objects '{qualifiedTable}' and '{qualifiedComponentsTable}' were not found. "
                + "The runtime source does not create database objects; apply the schema with Configlue.Source.PostgreSql.Migrations."
        );
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
    private readonly object _waiterGate = new();
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

    internal Action? TestHookOnRegisterAttempt { get; set; }

    internal Action? TestHookOnRegistered { get; set; }

    internal Action? TestHookOnCleanupRemoving { get; set; }

    internal int TestWaiterEntryCount
    {
        get
        {
            lock (_waiterGate)
            {
                return _waiters.Count;
            }
        }
    }

    public async ValueTask WaitForChangeAsync(
        string identity,
        string? observedRevision,
        Func<CancellationToken, ValueTask<ResourceReadResult>> readCurrent,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        TestHookOnRegisterAttempt?.Invoke();
        TaskCompletionSource signal;
        ConcurrentDictionary<long, TaskCompletionSource> identityWaiters;
        long waiterId;
        lock (_waiterGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_waiters.TryGetValue(identity, out identityWaiters!))
            {
                identityWaiters = new ConcurrentDictionary<long, TaskCompletionSource>();
                _waiters[identity] = identityWaiters;
            }

            waiterId = Interlocked.Increment(ref _nextWaiterId);
            identityWaiters[waiterId] = signal;
            TestHookOnRegistered?.Invoke();
        }

        try
        {
            EnsureListenerStarted();
            await _initialConnection.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

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
            lock (_waiterGate)
            {
                identityWaiters.TryRemove(waiterId, out _);
                TestHookOnCleanupRemoving?.Invoke();
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
