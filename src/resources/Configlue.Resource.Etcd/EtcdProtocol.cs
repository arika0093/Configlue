namespace Configlue.Resource.Etcd;

/// <summary>One key-value pair observed in etcd, with its modification metadata.</summary>
public sealed record EtcdKeyValue
{
    /// <summary>Creates an observed etcd key-value pair.</summary>
    /// <param name="key">The full etcd key.</param>
    /// <param name="value">The stored value bytes.</param>
    /// <param name="modRevision">The revision of the last modification of this key.</param>
    /// <param name="createRevision">The revision that created this key.</param>
    public EtcdKeyValue(
        string key,
        ReadOnlyMemory<byte> value,
        long modRevision,
        long createRevision
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (modRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modRevision));
        }

        if (createRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(createRevision));
        }

        Key = key;
        Value = value;
        ModRevision = modRevision;
        CreateRevision = createRevision;
    }

    /// <summary>The full etcd key.</summary>
    public string Key { get; }

    /// <summary>The stored value bytes.</summary>
    public ReadOnlyMemory<byte> Value { get; }

    /// <summary>The revision of the last modification of this key.</summary>
    public long ModRevision { get; }

    /// <summary>The revision that created this key.</summary>
    public long CreateRevision { get; }
}

/// <summary>The result of an etcd range (prefix) read.</summary>
/// <param name="Kvs">The key-value pairs under the requested prefix.</param>
/// <param name="HeaderRevision">The cluster revision observed by the read.</param>
public sealed record EtcdRangeResponse(IReadOnlyList<EtcdKeyValue> Kvs, long HeaderRevision)
{
    /// <summary>The key-value pairs under the requested prefix.</summary>
    public IReadOnlyList<EtcdKeyValue> Kvs { get; } =
        Kvs ?? throw new ArgumentNullException(nameof(Kvs));

    /// <summary>The cluster revision observed by the read.</summary>
    public long HeaderRevision { get; } =
        HeaderRevision <= 0
            ? throw new ArgumentOutOfRangeException(nameof(HeaderRevision))
            : HeaderRevision;
}

/// <summary>The kind of precondition an etcd transaction compare expresses.</summary>
public enum EtcdCompareKind
{
    /// <summary>Requires the key's modification revision to equal the expected value.</summary>
    ModRevisionEqual,

    /// <summary>Requires the key to be absent (its modification revision is zero).</summary>
    KeyNotExists,
}

/// <summary>One compare clause of an etcd transaction.</summary>
/// <param name="Key">The etcd key the precondition applies to.</param>
/// <param name="Kind">The kind of precondition.</param>
/// <param name="ExpectedModRevision">The expected modification revision for equality compares.</param>
public sealed record EtcdCompare(string Key, EtcdCompareKind Kind, long ExpectedModRevision)
{
    /// <summary>The etcd key the precondition applies to.</summary>
    public string Key { get; } =
        string.IsNullOrEmpty(Key)
            ? throw new ArgumentException("An etcd key cannot be empty.", nameof(Key))
            : Key;
}

/// <summary>One write operation of an etcd transaction.</summary>
/// <param name="Key">The etcd key the operation applies to.</param>
public abstract record EtcdWrite(string Key)
{
    /// <summary>The etcd key the operation applies to.</summary>
    public string Key { get; } =
        string.IsNullOrEmpty(Key)
            ? throw new ArgumentException("An etcd key cannot be empty.", nameof(Key))
            : Key;
}

/// <summary>Stores a value at an etcd key.</summary>
/// <param name="Key">The etcd key to store.</param>
/// <param name="Value">The value bytes to store.</param>
public sealed record EtcdPut(string Key, ReadOnlyMemory<byte> Value) : EtcdWrite(Key);

/// <summary>Removes an etcd key.</summary>
/// <param name="Key">The etcd key to remove.</param>
public sealed record EtcdDelete(string Key) : EtcdWrite(Key);

/// <summary>The result of an etcd transaction.</summary>
/// <param name="Succeeded">Whether every compare held and the writes were applied.</param>
/// <param name="HeaderRevision">The cluster revision of the transaction.</param>
public sealed record EtcdTxnResponse(bool Succeeded, long HeaderRevision)
{
    /// <summary>The cluster revision of the transaction.</summary>
    public long HeaderRevision { get; } =
        HeaderRevision <= 0
            ? throw new ArgumentOutOfRangeException(nameof(HeaderRevision))
            : HeaderRevision;
}

/// <summary>The kind of an etcd watch event.</summary>
public enum EtcdWatchEventKind
{
    /// <summary>A key was created or updated.</summary>
    Put,

    /// <summary>A key was deleted.</summary>
    Delete,
}

/// <summary>One event delivered by an etcd watch.</summary>
/// <param name="Kind">Whether the key was stored or deleted.</param>
/// <param name="Key">The full etcd key.</param>
/// <param name="Value">The stored value bytes, empty for deletions.</param>
/// <param name="ModRevision">The modification revision of the event.</param>
public sealed record EtcdWatchEvent(
    EtcdWatchEventKind Kind,
    string Key,
    ReadOnlyMemory<byte> Value,
    long ModRevision
);

/// <summary>One message delivered by an etcd watch stream.</summary>
/// <param name="Events">The events carried by this message, empty for progress notifications.</param>
/// <param name="HeaderRevision">The cluster revision of the message.</param>
/// <param name="IsProgressNotification">Whether this message carries no events.</param>
public sealed record EtcdWatchResponse(
    IReadOnlyList<EtcdWatchEvent> Events,
    long HeaderRevision,
    bool IsProgressNotification
)
{
    /// <summary>The events carried by this message, empty for progress notifications.</summary>
    public IReadOnlyList<EtcdWatchEvent> Events { get; } =
        Events ?? throw new ArgumentNullException(nameof(Events));
}

/// <summary>A failure reported by an etcd endpoint.</summary>
public class EtcdException : Exception
{
    /// <summary>Creates an etcd failure.</summary>
    /// <param name="message">The failure description without credentials.</param>
    public EtcdException(string message)
        : base(message) { }

    /// <summary>Creates an etcd failure with its cause.</summary>
    /// <param name="message">The failure description without credentials.</param>
    /// <param name="innerException">The underlying cause.</param>
    public EtcdException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>A transient etcd failure after which watching may resume from the last observed revision.</summary>
public sealed class EtcdTransientException : EtcdException
{
    /// <summary>Creates a transient etcd failure.</summary>
    /// <param name="message">The failure description without credentials.</param>
    public EtcdTransientException(string message)
        : base(message) { }

    /// <summary>Creates a transient etcd failure with its cause.</summary>
    /// <param name="message">The failure description without credentials.</param>
    /// <param name="innerException">The underlying cause.</param>
    public EtcdTransientException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// The requested watch revision was compacted away. The watcher must perform a fresh
/// range read to observe current state and resubscribe from the new revision.
/// </summary>
public sealed class EtcdCompactedException : EtcdException
{
    /// <summary>Creates a compaction failure.</summary>
    /// <param name="compactRevision">The revision at which history was compacted.</param>
    public EtcdCompactedException(long compactRevision)
        : base($"The etcd watch revision was compacted at revision {compactRevision}.")
    {
        CompactRevision = compactRevision;
    }

    /// <summary>The revision at which history was compacted.</summary>
    public long CompactRevision { get; }
}

/// <summary>Reads etcd key ranges and commits compare-and-swap transactions.</summary>
public interface IEtcdKvClient
{
    /// <summary>Reads the keys under a prefix at an optional revision.</summary>
    /// <param name="prefix">The key prefix to read.</param>
    /// <param name="revision">An optional revision for a consistent read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<EtcdRangeResponse> GetPrefixAsync(
        string prefix,
        long? revision = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Commits an atomic compare-and-swap transaction.</summary>
    /// <param name="compares">The preconditions that must all hold.</param>
    /// <param name="writes">The writes applied when every compare holds.</param>
    /// <param name="cancellationToken">Cancels the transaction.</param>
    ValueTask<EtcdTxnResponse> TransactAsync(
        IReadOnlyList<EtcdCompare> compares,
        IReadOnlyList<EtcdWrite> writes,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Streams prefix change notifications from etcd.</summary>
public interface IEtcdWatcherClient
{
    /// <summary>
    /// Watches keys under <paramref name="prefix"/> starting after <paramref name="startRevision"/>.
    /// A null revision watches only future changes. The handler returns <see langword="true"/>
    /// to stop watching because a relevant change was observed; progress notifications
    /// carry no events and must be tolerated.
    /// </summary>
    /// <param name="prefix">The key prefix to watch.</param>
    /// <param name="startRevision">The revision to resume from, or null for future changes.</param>
    /// <param name="onResponse">Handles each watch message.</param>
    /// <param name="cancellationToken">Cancels the watch.</param>
    Task WatchPrefixAsync(
        string prefix,
        long? startRevision,
        Func<EtcdWatchResponse, CancellationToken, ValueTask<bool>> onResponse,
        CancellationToken cancellationToken = default
    );
}

/// <summary>An injected, caller-owned etcd v3 client combining reads, transactions, and watches.</summary>
public interface IEtcdClient : IEtcdKvClient, IEtcdWatcherClient, IDisposable;
