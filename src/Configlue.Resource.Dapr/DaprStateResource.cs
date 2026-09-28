using System.Security.Cryptography;
using System.Text;
using Dapr.Client;

namespace Configlue.Resource.Dapr;

/// <summary>Reads and writes one byte resource through a Dapr State Management store.</summary>
/// <remarks>
/// The Dapr SDK does not return a revision from writes. Reads expose the store ETag when available;
/// conditional writes pass Configlue's expected revision back to Dapr and report ETag mismatches
/// as <see cref="StateConflictException"/>.
/// </remarks>
public sealed class DaprStateResource : IResourceReader, IResourceWriter, IResourceIdentity
{
    private readonly IDaprStateClient _client;
    private readonly DaprStateResourceOptions _options;

    /// <summary>Creates a resource for one Dapr state store key.</summary>
    public DaprStateResource(
        DaprClient client,
        string storeName,
        string key,
        DaprStateResourceOptions? options = null
    )
        : this(new DaprStateClient(client), storeName, key, options) { }

    internal DaprStateResource(
        IDaprStateClient client,
        string storeName,
        string key,
        DaprStateResourceOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _client = client;
        _options = options ?? new DaprStateResourceOptions();
        StoreName = storeName;
        Key = key;
        ResourceId = _options.ResourceId ?? CreateResourceId(storeName, key);
    }

    /// <summary>The Dapr state store name.</summary>
    public string StoreName { get; }

    /// <summary>The key stored in the Dapr state store.</summary>
    public string Key { get; }

    /// <inheritdoc />
    public ResourceId ResourceId { get; }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        var (content, etag) = await _client
            .GetByteStateAndETagAsync(
                StoreName,
                Key,
                _options.ConsistencyMode,
                _options.Metadata,
                cancellationToken
            )
            .ConfigureAwait(false);
        var revision = string.IsNullOrEmpty(etag) ? null : etag;
        return content.IsEmpty && revision is null
            ? ResourceReadResult.NotFound()
            : ResourceReadResult.Success(content, revision);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var checkRevision = request.CheckRevision || request.ExpectedRevision is not null;
        if (!checkRevision)
        {
            await _client
                .SaveByteStateAsync(
                    StoreName,
                    Key,
                    request.Content,
                    CreateStateOptions(ConcurrencyMode.LastWrite),
                    _options.Metadata,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return new StateWriteResult(null);
        }

        var expectedRevision = request.ExpectedRevision ?? string.Empty;
        var saved = await _client
            .TrySaveByteStateAsync(
                StoreName,
                Key,
                request.Content,
                expectedRevision,
                CreateStateOptions(ConcurrencyMode.FirstWrite),
                _options.Metadata,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!saved)
        {
            throw new StateConflictException(
                $"The Dapr state resource '{StoreName}/{Key}' changed after it was read."
            );
        }

        return new StateWriteResult(null);
    }

    private StateOptions CreateStateOptions(ConcurrencyMode concurrency) =>
        new() { Concurrency = concurrency, Consistency = _options.ConsistencyMode };

    private static ResourceId CreateResourceId(string storeName, string key)
    {
        var identity = Encoding.UTF8.GetBytes(storeName + "\n" + key);
        return new ResourceId(
            $"dapr:{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}"
        );
    }
}

internal interface IDaprStateClient
{
    Task<(ReadOnlyMemory<byte> Content, string? ETag)> GetByteStateAndETagAsync(
        string storeName,
        string key,
        ConsistencyMode? consistencyMode,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken
    );

    Task SaveByteStateAsync(
        string storeName,
        string key,
        ReadOnlyMemory<byte> content,
        StateOptions stateOptions,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken
    );

    Task<bool> TrySaveByteStateAsync(
        string storeName,
        string key,
        ReadOnlyMemory<byte> content,
        string etag,
        StateOptions stateOptions,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken
    );
}

internal sealed class DaprStateClient(DaprClient client) : IDaprStateClient
{
    public async Task<(ReadOnlyMemory<byte> Content, string? ETag)> GetByteStateAndETagAsync(
        string storeName,
        string key,
        ConsistencyMode? consistencyMode,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken
    )
    {
        var result = await client
            .GetByteStateAndETagAsync(storeName, key, consistencyMode, metadata, cancellationToken)
            .ConfigureAwait(false);
        return (result.Item1, result.etag);
    }

    public Task SaveByteStateAsync(
        string storeName,
        string key,
        ReadOnlyMemory<byte> content,
        StateOptions stateOptions,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken
    ) =>
        client.SaveByteStateAsync(
            storeName,
            key,
            content,
            stateOptions,
            metadata,
            cancellationToken
        );

    public Task<bool> TrySaveByteStateAsync(
        string storeName,
        string key,
        ReadOnlyMemory<byte> content,
        string etag,
        StateOptions stateOptions,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken
    ) =>
        client.TrySaveByteStateAsync(
            storeName,
            key,
            content,
            etag,
            stateOptions,
            metadata,
            cancellationToken
        );
}
