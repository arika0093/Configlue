namespace Configlue.Resource.Vault;

/// <summary>
/// The injectable Vault KV transport. Implementations remain caller-owned and carry their
/// own authentication (token, AppRole, Kubernetes auth, or custom renewal) externally;
/// the resource never performs login and never sees tokens.
/// </summary>
/// <remarks>
/// <para>
/// No login model is baked in: inject a preconfigured client (for example an
/// <see cref="HttpClient"/> wrapper that injects an <c>X-Vault-Token</c> header) or a
/// factory that resolves one per operation. Tokens must live in the transport, never in
/// resource options, provenance, or diagnostics.
/// </para>
/// <para>
/// Implementations must never include secret values or tokens in exception messages, and
/// should surface stale-version writes as <see cref="VaultKvConflictException"/> and
/// retryable server failures as <see cref="VaultKvTransientException"/>. Missing,
/// deleted, or destroyed secrets are reported as <see langword="null"/> results.
/// </para>
/// </remarks>
public interface IVaultKvClient
{
    /// <summary>Reads one secret payload with its version.</summary>
    /// <returns>
    /// The secret, or <see langword="null"/> when the path is missing, deleted, or destroyed.
    /// </returns>
    Task<VaultKvSecret?> ReadSecretAsync(
        string mount,
        string path,
        long? version,
        CancellationToken cancellationToken
    );

    /// <summary>Reads one secret's version metadata without its payload.</summary>
    /// <returns>
    /// The metadata, or <see langword="null"/> when the path is missing, deleted, or destroyed.
    /// </returns>
    Task<VaultKvSecretMetadata?> ReadMetadataAsync(
        string mount,
        string path,
        CancellationToken cancellationToken
    );

    /// <summary>Writes one secret payload.</summary>
    /// <param name="mount">The KV mount.</param>
    /// <param name="path">The secret path within the mount.</param>
    /// <param name="content">The payload to persist.</param>
    /// <param name="expectedVersion">
    /// The version the write must apply to (KV v2 check-and-set), or <see langword="null"/>
    /// for an unchecked write.
    /// </param>
    /// <param name="requireMissing">
    /// Requires that no secret currently exists (KV v2 check-and-set against version zero).
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new version, or <see langword="null"/> when the engine reports none (KV v1).</returns>
    /// <exception cref="VaultKvConflictException">The expected version is stale.</exception>
    Task<long?> WriteSecretAsync(
        string mount,
        string path,
        ReadOnlyMemory<byte> content,
        long? expectedVersion,
        bool requireMissing,
        CancellationToken cancellationToken
    );
}
