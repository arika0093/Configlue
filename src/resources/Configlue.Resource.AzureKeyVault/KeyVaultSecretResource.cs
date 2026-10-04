using System.Security.Cryptography;
using System.Text;
using Azure.Security.KeyVault.Secrets;

namespace Configlue.Resource.AzureKeyVault;

/// <summary>Reads and writes one byte resource through a single Azure Key Vault secret.</summary>
/// <remarks>
/// <para>
/// The secret value is UTF-8 encoded bytes. Safe metadata (secret name, version, enabled
/// state, timestamps) is exposed as the resource revision; secret values never appear in
/// revisions, diagnostics, or exception messages.
/// </para>
/// <para>
/// Writes are unconditional Key Vault upserts and provide no compare-and-swap.
/// Conditional writes are rejected rather than emulated with a read-then-write race.
/// </para>
/// </remarks>
public sealed class KeyVaultSecretResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        IResourceIdentity,
        IDisposable
{
    private readonly IKeyVaultSecretClient _client;
    private readonly Uri _vaultUri;
    private readonly KeyVaultSecretResourceOptions _options;
    private int _disposed;

    /// <summary>Creates a resource for one secret.</summary>
    /// <param name="client">The Azure SDK client. It remains caller-owned.</param>
    /// <param name="vaultUri">The vault URI.</param>
    /// <param name="secretName">The secret name.</param>
    /// <param name="secretVersion">A fixed version, or null for the current version.</param>
    /// <param name="options">Resource identity settings.</param>
    public KeyVaultSecretResource(
        SecretClient client,
        Uri vaultUri,
        string secretName,
        string? secretVersion = null,
        KeyVaultSecretResourceOptions? options = null
    )
        : this(new SecretClientAdapter(client), vaultUri, secretName, secretVersion, options) { }

    /// <summary>Creates a resource over an internal transport. Tests use this with fakes.</summary>
    internal KeyVaultSecretResource(
        IKeyVaultSecretClient client,
        Uri vaultUri,
        string secretName,
        string? secretVersion = null,
        KeyVaultSecretResourceOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(vaultUri);
        KeyVaultSecretName.Validate(secretName);
        if (secretVersion is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(secretVersion);
        }

        _client = client;
        _vaultUri = vaultUri;
        SecretName = secretName;
        SecretVersion = secretVersion;
        _options = options ?? new KeyVaultSecretResourceOptions();
    }

    /// <summary>The configured secret name.</summary>
    public string SecretName { get; }

    /// <summary>The fixed secret version, or null for the current version.</summary>
    public string? SecretVersion { get; }

    /// <summary>Whether this resource always reads a fixed, immutable version.</summary>
    public bool IsFixedVersion => ResolveVersion(ConfiglueResourceContext.Default) is not null;

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context)
    {
        if (_options.FixedResourceId is { } explicitId)
        {
            return explicitId;
        }

        return CreateResourceId(_vaultUri.Host, ResolveName(context), ResolveVersion(context));
    }

    /// <inheritdoc />
    public bool IsPipelineReadPreferred => true;

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var read = await ReadAsync(context, cancellationToken).ConfigureAwait(false);
        return read.Status switch
        {
            StateReadStatus.Success => PipelineResourceReader.FromStream(
                new MemoryStream(read.Content.ToArray(), writable: false),
                read.Revision,
                read.Schema
            ),
            StateReadStatus.NotFound => PipelineResourceReadResult.NotFound(read.Revision),
            StateReadStatus.Unavailable => PipelineResourceReadResult.Unavailable(read.Revision),
            _ => PipelineResourceReadResult.InvalidPayload(read.Revision),
        };
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var secretName = ResolveName(context);
        var version = ResolveVersion(context);
        KeyVaultSecretResult secret;
        try
        {
            secret = await _client
                .GetSecretAsync(secretName, version, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KeyVaultSecretNotFoundException)
        {
            return ResourceReadResult.NotFound(RevisionForVersion(version));
        }
        catch (KeyVaultSecretUnavailableException)
        {
            return ResourceReadResult.Unavailable(RevisionForVersion(version));
        }

        if (!secret.Metadata.Enabled)
        {
            return ResourceReadResult.NotFound(RevisionForVersion(secret.Metadata.Version));
        }

        var content = Encoding.UTF8.GetBytes(secret.Value);
        return ResourceReadResult.Success(content, RevisionForVersion(secret.Metadata.Version));
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!request.Condition.IsNone)
        {
            throw new InvalidOperationException(
                $"The Key Vault secret '{ResolveName(context)}' does not support conditional writes. "
                    + "Key Vault SetSecret is an unconditional upsert with no compare-and-swap; "
                    + "retry with an unchecked write or route the write through a store with CAS support."
            );
        }

        var secretName = ResolveName(context);
        string value;
        try
        {
            value = Encoding.UTF8.GetString(request.Content.Span);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"The Key Vault secret '{secretName}' could not encode the write payload as UTF-8.",
                exception
            );
        }

        KeyVaultSecretMetadata metadata;
        try
        {
            metadata = await _client
                .SetSecretAsync(secretName, value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KeyVaultSecretUnavailableException exception)
        {
            throw new InvalidOperationException(
                $"The Key Vault secret '{secretName}' is temporarily unavailable.",
                exception
            );
        }
        catch (KeyVaultSecretException exception)
        {
            throw new InvalidOperationException(
                $"The Key Vault secret '{secretName}' could not be written.",
                exception
            );
        }

        return new StateWriteResult(RevisionForVersion(metadata.Version));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"KeyVaultSecretResource(vault={_vaultUri.Host}, secret={SecretName}, version={SecretVersion ?? "<current>"})";

    private string ResolveName(ConfiglueResourceContext context)
    {
        var name = _options.SecretNameSelector?.Invoke(context) ?? SecretName;
        return KeyVaultSecretName.Validate(name);
    }

    private string? ResolveVersion(ConfiglueResourceContext context)
    {
        var version = _options.SecretVersionSelector?.Invoke(context) ?? SecretVersion;
        if (version is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }

        return version;
    }

    internal static string? RevisionForVersion(string? version) =>
        version is null ? null : $"keyvault:{version}";

    internal static ResourceId CreateResourceId(
        string vaultHost,
        string secretName,
        string? version
    )
    {
        var identity = Encoding.UTF8.GetBytes(
            version is null
                ? vaultHost + "\n" + secretName
                : vaultHost + "\n" + secretName + "\n" + version
        );
        return new ResourceId(
            $"keyvault:{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}"
        );
    }
}
