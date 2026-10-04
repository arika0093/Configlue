namespace Configlue.Source.Consul;

/// <summary>
/// Internal Consul KV transport. Implementations remain caller-owned; the source never disposes them.
/// ACL tokens are supplied to the implementation and are never logged or exposed in provenance.
/// </summary>
/// <remarks>
/// Production code never implements this interface directly: inject a configured
/// <see cref="HttpClient"/> plus the agent address and optional ACL token through the
/// resource/source surface, and the package adapts them internally. Tests implement this
/// interface with in-memory fakes or drive the internal HTTP transport with a fake handler.
/// </remarks>
internal interface IConsulKvClient
{
    /// <summary>Recursively reads every entry under <paramref name="prefix"/>.</summary>
    /// <remarks>
    /// A missing prefix is reported as an empty entry list rather than an error, so callers can
    /// distinguish absence from failure. Blocking queries set <see cref="ConsulKvListOptions.WaitIndex"/>
    /// and <see cref="ConsulKvListOptions.WaitTimeout"/>; a timeout or spurious wakeup returns the
    /// current index without new entries.
    /// </remarks>
    Task<ConsulKvListResult> ListAsync(
        string prefix,
        ConsulKvListOptions? options,
        CancellationToken cancellationToken
    );

    /// <summary>Reads one key without recursion.</summary>
    Task<ConsulKvListResult> GetAsync(
        string key,
        ConsulKvListOptions? options,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Writes one key. A null <paramref name="cas"/> writes unconditionally, 0 requires absence,
    /// and any other value requires the entry's modify index to match.
    /// </summary>
    /// <returns>Whether the write was applied and the new Consul index.</returns>
    Task<(bool Applied, ulong NewIndex)> PutAsync(
        string key,
        ReadOnlyMemory<byte> value,
        ulong? cas,
        ConsulKvWriteOptions? options,
        CancellationToken cancellationToken
    );

    /// <summary>Deletes one key with optional CAS semantics.</summary>
    /// <returns>Whether the delete was applied and the new Consul index.</returns>
    Task<(bool Applied, ulong NewIndex)> DeleteAsync(
        string key,
        ulong? cas,
        ConsulKvWriteOptions? options,
        CancellationToken cancellationToken
    );

    /// <summary>Applies several key operations atomically via the Consul transaction API.</summary>
    Task<ConsulTxnResult> TransactAsync(
        IReadOnlyList<ConsulTxnOperation> operations,
        ConsulKvWriteOptions? options,
        CancellationToken cancellationToken
    );
}
