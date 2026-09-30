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
public sealed class DaprStateResource
    : IContextualResourceReader,
        IContextualPipelineResourceReader,
        IContextualResourceWriter,
        IContextualResourceIdentity
{
    private readonly IDaprStateClient _client;
    private readonly DaprStateResourceOptions _options;
    private readonly Func<ConfiglueResourceContext, IDaprStateClient>? _clientSelector;

    /// <summary>Creates a resource for one Dapr state store key.</summary>
    public DaprStateResource(
        DaprClient client,
        string storeName,
        string key,
        DaprStateResourceOptions? options = null
    )
        : this(
            new DaprStateClient(client),
            storeName,
            key,
            options,
            options?.ClientSelector is { } selector
                ? context => new DaprStateClient(selector(context))
                : null
        ) { }

    internal DaprStateResource(
        IDaprStateClient client,
        string storeName,
        string key,
        DaprStateResourceOptions? options = null,
        Func<ConfiglueResourceContext, IDaprStateClient>? clientSelector = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _client = client;
        _options = options ?? new DaprStateResourceOptions();
        _clientSelector = clientSelector;
        StoreName = storeName;
        Key = key;
        ResourceId = _options.ResourceId ?? GetResourceId(ConfiglueResourceContext.Default);
    }

    /// <summary>The Dapr state store name.</summary>
    public string StoreName { get; }

    /// <summary>The key stored in the Dapr state store.</summary>
    public string Key { get; }

    /// <inheritdoc />
    public ResourceId ResourceId { get; }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _options.ResourceId
        ?? CreateResourceId(
            ResolveStoreName(context),
            ResolveKey(context),
            _clientSelector is null ? null : context.Route.Value
        );

    /// <inheritdoc />
    public bool IsPipelineReadPreferred => false;

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        CancellationToken cancellationToken = default
    )
    {
        var result = await ReadAsync(ConfiglueResourceContext.Default, cancellationToken)
            .ConfigureAwait(false);
        return await PipelineResourceReader
            .FromMemoryAsync(result, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var result = await ReadAsync(context, cancellationToken).ConfigureAwait(false);
        return await PipelineResourceReader
            .FromMemoryAsync(result, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    ) => await ReadAsync(ConfiglueResourceContext.Default, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var storeName = ResolveStoreName(context);
        var key = ResolveKey(context);
        var (content, etag) = await GetClient(context)
            .GetByteStateAndETagAsync(
                storeName,
                key,
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
    ) =>
        await WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var storeName = ResolveStoreName(context);
        var key = ResolveKey(context);
        var client = GetClient(context);
        var checkRevision = !request.Condition.IsNone;
        if (!checkRevision)
        {
            await client
                .SaveByteStateAsync(
                    storeName,
                    key,
                    request.Content,
                    CreateStateOptions(ConcurrencyMode.LastWrite),
                    _options.Metadata,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return new StateWriteResult(null);
        }

        var expectedRevision = request.Condition.Revision ?? string.Empty;
        var saved = await client
            .TrySaveByteStateAsync(
                storeName,
                key,
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
                $"The Dapr state resource '{storeName}/{key}' changed after it was read."
            );
        }

        return new StateWriteResult(null);
    }

    private StateOptions CreateStateOptions(ConcurrencyMode concurrency) =>
        new() { Concurrency = concurrency, Consistency = _options.ConsistencyMode };

    private IDaprStateClient GetClient(ConfiglueResourceContext context) =>
        _clientSelector?.Invoke(context) ?? _client;

    private string ResolveStoreName(ConfiglueResourceContext context)
    {
        var storeName = _options.StoreNameSelector?.Invoke(context) ?? StoreName;
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        return storeName;
    }

    private string ResolveKey(ConfiglueResourceContext context)
    {
        var key = _options.KeySelector?.Invoke(context) ?? Key;
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return key;
    }

    private static ResourceId CreateResourceId(string storeName, string key, string? route = null)
    {
        var identity = Encoding.UTF8.GetBytes(
            route is null ? storeName + "\n" + key : storeName + "\n" + key + "\n" + route
        );
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
