using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Configlue.Internal;
using Configlue.Sources;

namespace Configlue.Resource.Vault;

/// <summary>Reads and writes one serialized payload through a HashiCorp Vault KV secret.</summary>
/// <remarks>
/// <para>
/// KV v2 secret versions are exposed as revisions and used for check-and-set writes; a stale
/// expected version becomes a normal Configlue conflict instead of overwriting a newer secret.
/// KV v1 has no versions: reads report a content-hash revision and writes are unchecked except
/// for existence checks, which are documented as non-atomic.
/// </para>
/// <para>
/// Vault KV provides no streaming watch. <see cref="WaitForChangeAsync"/> polls secret metadata
/// (v2) or content (v1) at <see cref="VaultKvResourceOptions.PollingInterval"/> without
/// re-downloading payloads when metadata is sufficient. Secret values and Vault tokens never
/// appear in exceptions, diagnostics, provenance, or <see cref="ToString"/>.
/// </para>
/// <para>
/// Vault performs no multi-path transactions, so this resource intentionally does not implement
/// batch writing: commits spanning several Vault paths keep the normal partial-write semantics
/// used elsewhere in Configlue.
/// </para>
/// </remarks>
public sealed class VaultKvResource
    : IResourceReader,
        IResourceWriter,
        IResourceIdentity,
        ISourceWatcher,
        IDisposable
{
    private readonly IVaultKvClient _client;
    private readonly VaultKvResourceOptions _options;
    private readonly Func<ConfiglueResourceContext, IVaultKvClient>? _clientSelector;
    private readonly WatchShutdown _watchShutdown = new();
    private int _disposed;

    /// <summary>Creates a resource for one secret in a Vault KV mount.</summary>
    public VaultKvResource(
        IVaultKvClient client,
        string mount,
        string path,
        VaultKvResourceOptions? options = null
    )
        : this(
            client,
            mount,
            path,
            options,
            options?.ClientSelector is { } selector
                ? context =>
                    selector(context)
                    ?? throw new InvalidOperationException(
                        "The Vault client selector returned null."
                    )
                : null
        ) { }

    internal VaultKvResource(
        IVaultKvClient client,
        string mount,
        string path,
        VaultKvResourceOptions? options,
        Func<ConfiglueResourceContext, IVaultKvClient>? clientSelector
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        VaultKvPath.ValidateMount(mount);
        VaultKvPath.ValidatePath(path);

        _client = client;
        _options = options ?? new VaultKvResourceOptions();
        VaultKvResourceOptions.Validate(_options, nameof(options));
        _clientSelector = clientSelector;
        Mount = mount;
        Path = path;
    }

    /// <summary>The KV mount.</summary>
    public string Mount { get; }

    /// <summary>The secret path within the mount.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _options.FixedResourceId
        ?? CreateResourceId(
            ResolveMount(context),
            ResolvePath(context),
            _clientSelector is null ? null : context.Route.Value
        );

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var address = ResolveAddress(context);
        var secret = await address
            .Client.ReadSecretAsync(address.Mount, address.Path, null, cancellationToken)
            .ConfigureAwait(false);
        if (secret is null)
        {
            return ResourceReadResult.NotFound();
        }

        if (_options.KvVersion == VaultKvEngine.V1)
        {
            return ResourceReadResult.Success(secret.Content, GetContentRevision(secret.Content));
        }

        return ConvertReadResult(secret, address);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var address = ResolveAddress(context);
        if (_options.KvVersion == VaultKvEngine.V1)
        {
            return await WriteV1Async(address, request, cancellationToken).ConfigureAwait(false);
        }

        long? expectedVersion = null;
        var requireMissing = false;
        if (request.Condition.IsMustNotExist)
        {
            requireMissing = true;
        }
        else if (request.Condition.IsMatch)
        {
            expectedVersion = ParseExpectedVersion(request.Condition.Revision, address);
        }

        try
        {
            var version = await address
                .Client.WriteSecretAsync(
                    address.Mount,
                    address.Path,
                    request.Content,
                    expectedVersion,
                    requireMissing,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return new StateWriteResult(version?.ToString(CultureInfo.InvariantCulture));
        }
        catch (VaultKvConflictException)
        {
            throw CreateConflict(address);
        }
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var address = ResolveAddress(context);
        await _watchShutdown
            .WaitAsync(
                watchCancellationToken =>
                    PollingWatch.WaitForRevisionChangeAsync(
                        cancellation => new ValueTask<string?>(
                            GetCurrentRevisionAsync(address, cancellation)
                        ),
                        observedRevision,
                        _options.PollingInterval,
                        watchCancellationToken,
                        static exception => exception is VaultKvTransientException
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
    }

    /// <summary>Returns non-sensitive addressing; never secret values or tokens.</summary>
    public override string ToString() => $"VaultKv({Mount}/{Path})";

    private static async ValueTask<StateWriteResult> WriteV1Async(
        VaultKvAddress address,
        ResourceWriteRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Condition.IsMatch)
        {
            // KV v1 has no check-and-set. Compare against a fresh read so a stale baseline
            // still becomes a Configlue conflict; the read and write are not atomic.
            var current = await address
                .Client.ReadSecretAsync(address.Mount, address.Path, null, cancellationToken)
                .ConfigureAwait(false);
            var currentRevision = current is null ? null : GetContentRevision(current.Content);
            if (
                !string.Equals(
                    currentRevision,
                    request.Condition.Revision,
                    StringComparison.Ordinal
                )
            )
            {
                throw CreateConflict(address);
            }
        }

        try
        {
            await address
                .Client.WriteSecretAsync(
                    address.Mount,
                    address.Path,
                    request.Content,
                    null,
                    request.Condition.IsMustNotExist,
                    cancellationToken
                )
                .ConfigureAwait(false);
            // KV v1 reports no versions, so the content hash is the revision everywhere.
            return new StateWriteResult(GetContentRevision(request.Content));
        }
        catch (VaultKvConflictException)
        {
            throw CreateConflict(address);
        }
    }

    private async Task<string?> GetCurrentRevisionAsync(
        VaultKvAddress address,
        CancellationToken cancellationToken
    )
    {
        if (_options.KvVersion == VaultKvEngine.V2)
        {
            var metadata = await address
                .Client.ReadMetadataAsync(address.Mount, address.Path, cancellationToken)
                .ConfigureAwait(false);
            if (metadata is null)
            {
                return null;
            }

            if (metadata.Version is not { } version || version <= 0)
            {
                throw new InvalidDataException(
                    $"The Vault secret '{address.Mount}/{address.Path}' has an invalid version."
                );
            }

            return version.ToString(CultureInfo.InvariantCulture);
        }

        var secret = await address
            .Client.ReadSecretAsync(address.Mount, address.Path, null, cancellationToken)
            .ConfigureAwait(false);
        return secret is null ? null : GetContentRevision(secret.Content);
    }

    private VaultKvAddress ResolveAddress(ConfiglueResourceContext context)
    {
        var mount = ResolveMount(context);
        var path = ResolvePath(context);
        var client = _clientSelector?.Invoke(context) ?? _client;
        return new VaultKvAddress(mount, path, client);
    }

    private string ResolveMount(ConfiglueResourceContext context)
    {
        var mount = _options.MountSelector?.Invoke(context) ?? Mount;
        VaultKvPath.ValidateMount(mount);
        return mount;
    }

    private string ResolvePath(ConfiglueResourceContext context)
    {
        var path = _options.PathSelector?.Invoke(context) ?? Path;
        VaultKvPath.ValidatePath(path);
        return path;
    }

    private static ResourceReadResult ConvertReadResult(
        VaultKvSecret secret,
        VaultKvAddress address
    )
    {
        if (secret.Version is not { } version || version <= 0)
        {
            throw new InvalidDataException(
                $"The Vault secret '{address.Mount}/{address.Path}' has an invalid version."
            );
        }

        return ResourceReadResult.Success(
            secret.Content,
            version.ToString(CultureInfo.InvariantCulture)
        );
    }

    private static long ParseExpectedVersion(string? revision, VaultKvAddress address)
    {
        if (
            revision is null
            || !long.TryParse(
                revision,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed
            )
            || parsed <= 0
        )
        {
            throw new StateConflictException(
                $"The Vault secret '{address.Mount}/{address.Path}' no longer matches its expected version."
            );
        }

        return parsed;
    }

    private static StateConflictException CreateConflict(VaultKvAddress address) =>
        new($"The Vault secret '{address.Mount}/{address.Path}' changed after it was read.");

    private static string GetContentRevision(ReadOnlyMemory<byte> content)
    {
#if NETSTANDARD
        using var algorithm = SHA256.Create();
        var bytes = content.ToArray();
        return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "");
#else
        return Convert.ToHexString(SHA256.HashData(content.Span));
#endif
    }

    private static ResourceId CreateResourceId(string mount, string path, string? route = null)
    {
        var identity = Encoding.UTF8.GetBytes(
            route is null ? mount + "\n" + path : mount + "\n" + path + "\n" + route
        );
        return new ResourceId(
            $"vault:{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}"
        );
    }

    private sealed record VaultKvAddress(string Mount, string Path, IVaultKvClient Client);
}
