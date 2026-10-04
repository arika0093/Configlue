namespace Configlue.Source.Consul;

/// <summary>One Consul KV entry with the metadata required for revision tracking.</summary>
public sealed record ConsulKvEntry
{
    /// <summary>Creates a Consul KV entry.</summary>
    public ConsulKvEntry(string key, byte[]? value, ulong modifyIndex, ulong createIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        Value = value;
        ModifyIndex = modifyIndex;
        CreateIndex = createIndex;
    }

    /// <summary>The full Consul key.</summary>
    public string Key { get; }

    /// <summary>The raw value bytes; null when the entry has no value.</summary>
    public byte[]? Value { get; }

    /// <summary>The per-entry modification index used for CAS and revision tracking.</summary>
    public ulong ModifyIndex { get; }

    /// <summary>The creation index.</summary>
    public ulong CreateIndex { get; }
}

/// <summary>The result of a recursive Consul KV read.</summary>
public sealed record ConsulKvListResult
{
    /// <summary>Creates a list result.</summary>
    public ConsulKvListResult(IReadOnlyList<ConsulKvEntry> entries, ulong consulIndex)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = entries;
        ConsulIndex = consulIndex;
    }

    /// <summary>The entries under the requested prefix.</summary>
    public IReadOnlyList<ConsulKvEntry> Entries { get; }

    /// <summary>The Consul index (<c>X-Consul-Index</c>) for the read.</summary>
    public ulong ConsulIndex { get; }
}

/// <summary>Options for a recursive Consul KV read, including blocking queries.</summary>
public sealed record ConsulKvListOptions
{
    /// <summary>The datacenter to query; null uses the agent default.</summary>
    public string? Datacenter { get; init; }

    /// <summary>The Consul namespace; null uses the default namespace.</summary>
    public string? Namespace { get; init; }

    /// <summary>The Consul partition; null uses the default partition.</summary>
    public string? Partition { get; init; }

    /// <summary>The read consistency mode.</summary>
    public ConsulConsistencyMode Consistency { get; init; } = ConsulConsistencyMode.Default;

    /// <summary>When set, a blocking query waits until the index advances past this value.</summary>
    public ulong? WaitIndex { get; init; }

    /// <summary>How long a blocking query may wait (for example <c>5m</c>).</summary>
    public TimeSpan? WaitTimeout { get; init; }
}

/// <summary>Options for a Consul KV write.</summary>
public sealed record ConsulKvWriteOptions
{
    /// <summary>The datacenter to write to; null uses the agent default.</summary>
    public string? Datacenter { get; init; }

    /// <summary>The Consul namespace; null uses the default namespace.</summary>
    public string? Namespace { get; init; }

    /// <summary>The Consul partition; null uses the default partition.</summary>
    public string? Partition { get; init; }
}

/// <summary>One atomic Consul transaction operation.</summary>
public sealed record ConsulTxnOperation
{
    /// <summary>Creates a transaction operation.</summary>
    /// <param name="verb">Either <c>set</c> or <c>delete</c>.</param>
    /// <param name="key">The full Consul key.</param>
    /// <param name="value">The value for <c>set</c>; null for <c>delete</c>.</param>
    /// <param name="cas">Null for an unchecked write, 0 to require absence, otherwise the expected modify index.</param>
    public ConsulTxnOperation(string verb, string key, byte[]? value, ulong? cas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Verb = verb;
        Key = key;
        Value = value;
        Cas = cas;
    }

    /// <summary>Either <c>set</c> or <c>delete</c>.</summary>
    public string Verb { get; }

    /// <summary>The full Consul key.</summary>
    public string Key { get; }

    /// <summary>The value for <c>set</c>; null for <c>delete</c>.</summary>
    public byte[]? Value { get; }

    /// <summary>Null for an unchecked write, 0 to require absence, otherwise the expected modify index.</summary>
    public ulong? Cas { get; }

    /// <summary>Creates a <c>set</c> operation.</summary>
    public static ConsulTxnOperation Set(string key, byte[] value, ulong? cas = null) =>
        new("set", key, value, cas);

    /// <summary>Creates a <c>delete</c> operation.</summary>
    public static ConsulTxnOperation Delete(string key, ulong? cas = null) =>
        new("delete", key, null, cas);
}

/// <summary>The result of a Consul transaction.</summary>
public sealed record ConsulTxnResult
{
    /// <summary>Creates a transaction result.</summary>
    public ConsulTxnResult(bool committed, ulong consulIndex, string[]? errors = null)
    {
        ConsulIndex = consulIndex;
        Committed = committed;
        Errors = errors;
    }

    /// <summary>Whether every operation was applied atomically.</summary>
    public bool Committed { get; }

    /// <summary>The Consul index after the transaction.</summary>
    public ulong ConsulIndex { get; }

    /// <summary>Per-operation errors when the transaction was rejected.</summary>
    public string[]? Errors { get; }
}
