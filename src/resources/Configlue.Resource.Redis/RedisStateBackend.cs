#pragma warning disable S2077 // Redis key/channel names are controlled; row values are sent separately to Lua.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using StackExchange.Redis;

namespace Configlue.Resource.Redis;

internal sealed class RedisStateBackend : IRedisStateBackend
{
    private const string WriteScript = """
        local mode = ARGV[1]
        if mode == 'must-not-exist' and redis.call('EXISTS', KEYS[1]) ~= 0 then
            return 0
        end
        if mode == 'match' then
            local current = redis.call('HGET', KEYS[1], 'revision')
            if not current or current ~= ARGV[2] then
                return 0
            end
        end
        local revision = redis.call('HINCRBY', KEYS[1], 'revision', 1)
        redis.call('HSET', KEYS[1], 'payload', ARGV[3], 'updated_at', ARGV[4])
        if ARGV[7] == '1' then
            redis.call('HSET', KEYS[1], 'schema_model_id', ARGV[8], 'schema_version', ARGV[9])
        else
            redis.call('HDEL', KEYS[1], 'schema_model_id', 'schema_version')
        end
        redis.call('PUBLISH', ARGV[5], ARGV[6])
        return revision
        """;

    private static readonly RedisValue[] ReadFields =
    [
        "payload",
        "revision",
        "schema_model_id",
        "schema_version",
    ];

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisChangeHubLease _changeHub;
    private readonly ConcurrentBag<RedisValue[]> _writeArgumentBuffers = new();
    private readonly ConcurrentBag<RedisKey[]> _writeKeyBuffers = new();

    public RedisStateBackend(IConnectionMultiplexer multiplexer, RedisResourceOptions options)
    {
        _multiplexer = multiplexer;
        _changeHub = RedisChangeHubRegistry.Acquire(multiplexer, options.NotificationChannel);
    }

    public async ValueTask<ResourceReadResult> ReadAsync(
        RedisResourceAddress address,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = await GetDatabase(address.Database)
            .HashGetAsync(address.Key, ReadFields)
            .ConfigureAwait(false);
        if (values[1].IsNull)
        {
            if (values.Any(static value => !value.IsNull))
            {
                throw new InvalidDataException(
                    $"The Redis row '{address.Key}' has fields but no revision."
                );
            }

            return ResourceReadResult.NotFound();
        }

        if (values[0].IsNull)
        {
            throw new InvalidDataException($"The Redis row '{address.Key}' has no payload field.");
        }

        var revision = values[1].ToString();
        if (
            !long.TryParse(
                revision,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedRevision
            )
            || parsedRevision <= 0
        )
        {
            throw new InvalidDataException(
                $"The Redis row '{address.Key}' has an invalid revision."
            );
        }

        StateSchemaMetadata? schema = null;
        if (!values[2].IsNull || !values[3].IsNull)
        {
            string? modelId =
                values[2].IsNull || values[2].ToString().Length == 0 ? null : values[2].ToString();
            var schemaVersion = values[3].IsNull
                ? StateSchemaMetadata.InitialVersion
                : int.Parse(values[3].ToString(), CultureInfo.InvariantCulture);
            schema = new StateSchemaMetadata(modelId, schemaVersion);
        }

        ReadOnlyMemory<byte> content = values[0];
        return ResourceReadResult.Success(content, revision, schema);
    }

    public async ValueTask<StateWriteResult> WriteAsync(
        RedisResourceAddress address,
        ResourceWriteRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        string mode;
        if (request.Condition.IsNone)
        {
            mode = "none";
        }
        else if (request.Condition.IsMustNotExist)
        {
            mode = "must-not-exist";
        }
        else
        {
            mode = "match";
        }
        var expectedRevision = request.Condition.IsMatch
            ? request.Condition.Revision
            : string.Empty;
        if (
            request.Condition.IsMatch
            && (
                !long.TryParse(
                    expectedRevision,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expected
                )
                || expected <= 0
            )
        )
        {
            throw CreateConflict(address);
        }

        var schema = request.Schema;
        if (!_writeArgumentBuffers.TryTake(out var arguments))
        {
            arguments = new RedisValue[9];
        }
        if (!_writeKeyBuffers.TryTake(out var keys))
        {
            keys = new RedisKey[1];
        }
        long revision;
        try
        {
            keys[0] = address.Key;
            arguments[0] = mode;
            arguments[1] = expectedRevision;
            arguments[2] = request.Content;
            arguments[3] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            arguments[4] = address.NotificationChannel;
            arguments[5] = address.NotificationIdentity;
            arguments[6] = schema is null ? "0" : "1";
            arguments[7] = schema?.ModelId ?? string.Empty;
            arguments[8] = schema?.Version.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            var result = await GetDatabase(address.Database)
                .ScriptEvaluateAsync(WriteScript, keys, arguments)
                .ConfigureAwait(false);
            revision = (long)result;
        }
        finally
        {
            Array.Clear(arguments, 0, arguments.Length);
            Array.Clear(keys, 0, keys.Length);
            _writeArgumentBuffers.Add(arguments);
            _writeKeyBuffers.Add(keys);
        }
        if (revision <= 0)
        {
            throw CreateConflict(address);
        }

        return new StateWriteResult(revision.ToString(CultureInfo.InvariantCulture));
    }

    public ValueTask WaitForChangeAsync(
        RedisResourceAddress address,
        string? observedRevision,
        CancellationToken cancellationToken
    ) =>
        _changeHub.Hub.WaitForChangeAsync(
            address.NotificationIdentity,
            observedRevision,
            token => ReadAsync(address, token),
            cancellationToken
        );

    public void Dispose() => _changeHub.Dispose();

    private IDatabase GetDatabase(int database) => _multiplexer.GetDatabase(database);

    private static StateConflictException CreateConflict(RedisResourceAddress address) =>
        new($"The Redis resource '{address.Key}' no longer matches its expected revision.");
}

internal sealed class RedisChangeHub : IDisposable
{
    private readonly IRedisNotificationTransport _transport;
    private readonly ConcurrentDictionary<
        string,
        ConcurrentDictionary<long, TaskCompletionSource>
    > _waiters = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _disposedSignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly object _subscriptionGate = new();
    private Task? _subscriptionTask;
    private long _nextWaiterId;
    private int _disposed;

    public RedisChangeHub(IRedisNotificationTransport transport, string channel)
    {
        _transport = transport;
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
            var subscription = EnsureSubscriptionStarted();
            await Task.WhenAny(subscription, _disposedSignal.Task)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            await subscription.ConfigureAwait(false);
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
                (
                    (ICollection<
                        KeyValuePair<string, ConcurrentDictionary<long, TaskCompletionSource>>
                    >)
                        _waiters
                ).Remove(
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

        _disposedSignal.TrySetResult();
        SignalAllWaiters();
        _transport.Dispose();
    }

    private Task EnsureSubscriptionStarted()
    {
        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (
                _subscriptionTask is null
                || _subscriptionTask.IsFaulted
                || _subscriptionTask.IsCanceled
            )
            {
                _subscriptionTask = _transport.SubscribeAsync(
                    Channel,
                    OnNotification,
                    SignalAllWaiters
                );
            }

            return _subscriptionTask;
        }
    }

    private void OnNotification(string payload)
    {
        if (_waiters.TryGetValue(payload, out var identityWaiters))
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

internal interface IRedisNotificationTransport : IDisposable
{
    Task SubscribeAsync(string channel, Action<string> onNotification, Action onReconnect);
}

internal sealed class RedisNotificationTransport : IRedisNotificationTransport
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ISubscriber _subscriber;
    private RedisChannel? _channel;
    private Action<RedisChannel, RedisValue>? _handler;
    private Action? _onReconnect;
    private int _disposed;

    public RedisNotificationTransport(IConnectionMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
        _subscriber = multiplexer.GetSubscriber();
    }

    public async Task SubscribeAsync(
        string channel,
        Action<string> onNotification,
        Action onReconnect
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_channel is null)
        {
            _channel = RedisChannel.Literal(channel);
            _onReconnect = onReconnect;
            _handler = (_, message) => onNotification(message.ToString());
            _multiplexer.ConnectionRestored += OnConnectionRestored;
        }

        await _subscriber.SubscribeAsync(_channel.Value, _handler!).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _multiplexer.ConnectionRestored -= OnConnectionRestored;
        if (_channel is { } channel && _handler is { } handler)
        {
            try
            {
                _subscriber.Unsubscribe(channel, handler);
            }
            catch (RedisException exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }
        }
    }

    private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs args) =>
        _onReconnect?.Invoke();
}

internal static class RedisChangeHubRegistry
{
    private static readonly ConditionalWeakTable<IConnectionMultiplexer, HubRegistry> Registries =
        new();

    public static RedisChangeHubLease Acquire(IConnectionMultiplexer multiplexer, string channel) =>
        Registries
            .GetValue(multiplexer, static _ => new HubRegistry())
            .AcquireHub(multiplexer, channel);

    internal sealed class HubRegistry
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, HubEntry> _hubs = new(StringComparer.Ordinal);

        public RedisChangeHubLease AcquireHub(IConnectionMultiplexer multiplexer, string channel)
        {
            lock (_gate)
            {
                if (!_hubs.TryGetValue(channel, out var entry))
                {
                    var transport = new RedisNotificationTransport(multiplexer);
                    entry = new HubEntry(new RedisChangeHub(transport, channel));
                    _hubs.Add(channel, entry);
                }

                entry.LeaseCount++;
                return new RedisChangeHubLease(this, channel, entry.Hub);
            }
        }

        public void Release(string channel)
        {
            RedisChangeHub? hubToDispose = null;
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

    private sealed class HubEntry(RedisChangeHub hub)
    {
        public RedisChangeHub Hub { get; } = hub;

        public int LeaseCount { get; set; }
    }
}

internal sealed class RedisChangeHubLease : IDisposable
{
    private RedisChangeHubRegistry.HubRegistry? _registry;
    private readonly string _channel;

    internal RedisChangeHubLease(
        RedisChangeHubRegistry.HubRegistry registry,
        string channel,
        RedisChangeHub hub
    )
    {
        _registry = registry;
        _channel = channel;
        Hub = hub;
    }

    public RedisChangeHub Hub { get; }

    public void Dispose() => Interlocked.Exchange(ref _registry, null)?.Release(_channel);
}
