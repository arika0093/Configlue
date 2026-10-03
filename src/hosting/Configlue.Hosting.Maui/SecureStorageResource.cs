using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Maui.Storage;

namespace Configlue.Hosting.Maui;

/// <summary>Stores codec bytes as base64 in one MAUI SecureStorage entry for small secrets.</summary>
/// <remarks>
/// Revisions are content fingerprints. Conditional writes are serialized only between
/// these resources sharing the same ISecureStorage instance and key in this process.
/// Native stores do not provide cross-process CAS; direct native writes can race a
/// check-and-set. Schema metadata must be carried by the codec payload. No watcher,
/// batch transaction, or automatic plaintext backup is provided.
/// </remarks>
public sealed class SecureStorageResource : IResourceReader, IResourceWriter
{
    private static readonly ConditionalWeakTable<
        ISecureStorage,
        ConcurrentDictionary<string, SemaphoreSlim>
    > Gates = new();
    private readonly ISecureStorage _storage;
    private readonly string _key;
    private readonly int _maximumContentBytes;
    private readonly SemaphoreSlim _gate;

    /// <summary>Creates a small-secret resource with an injected native storage service.</summary>
    /// <param name="storage">The service; reuse the same instance for cooperating conditional writers.</param>
    /// <param name="key">The application-owned secure entry key.</param>
    /// <param name="maximumContentBytes">The raw payload limit, defaulting to 1 KiB. Native platform limits can be lower.</param>
    public SecureStorageResource(ISecureStorage storage, string key, int maximumContentBytes = 1024)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumContentBytes);
        _storage = storage;
        _key = key;
        _maximumContentBytes = maximumContentBytes;
        _gate = Gates
            .GetValue(
                storage,
                static _ => new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal)
            )
            .GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await _storage.GetAsync(_key).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (raw is null)
            return ResourceReadResult.NotFound();
        var revision = Fingerprint(raw);
        if (raw.Length > EncodedLengthLimit())
            return ResourceReadResult.InvalidPayload(revision);
        try
        {
            var bytes = Convert.FromBase64String(raw);
            return bytes.Length <= _maximumContentBytes
                ? ResourceReadResult.Success(bytes, revision)
                : ResourceReadResult.InvalidPayload(revision);
        }
        catch (FormatException)
        {
            return ResourceReadResult.InvalidPayload(revision);
        }
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Content.Length > _maximumContentBytes)
            throw new ArgumentException(
                "The payload exceeds this small-secret resource's byte limit.",
                nameof(request)
            );
        // Snapshot caller-owned memory before awaiting the native service or a cooperating writer.
        var encoded = Convert.ToBase64String(request.Content.ToArray());
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!request.Condition.IsNone)
            {
                var current = await _storage.GetAsync(_key).ConfigureAwait(false);
                if (
                    !request.Condition.IsSatisfiedBy(
                        current is null ? null : Fingerprint(current),
                        current is not null
                    )
                )
                    throw new StateConflictException(
                        "The secure entry no longer satisfies the requested revision condition."
                    );
            }
            cancellationToken.ThrowIfCancellationRequested();
            await _storage.SetAsync(_key, encoded).ConfigureAwait(false);
            // Native SetAsync cannot be canceled. Once it commits, report the committed revision.
            return new StateWriteResult(Fingerprint(encoded));
        }
        finally
        {
            _gate.Release();
        }
    }

    private long EncodedLengthLimit() => ((_maximumContentBytes + 2L) / 3L) * 4L;

    private static string Fingerprint(string encoded)
    {
        using var hash = SHA256.Create();
        return Convert.ToBase64String(
            hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(encoded))
        );
    }
}
