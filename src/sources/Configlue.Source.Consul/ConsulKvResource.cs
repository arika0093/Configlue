using System.Globalization;
using Configlue.Internal;

namespace Configlue.Source.Consul;

/// <summary>Reads and writes one byte resource through a Consul KV key.</summary>
/// <remarks>Modify indexes are exposed as revisions and used for CAS writes.</remarks>
public sealed class ConsulKvResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        IResourceIdentity,
        ISourceWatcher,
        IDisposable
{
    private readonly IConsulKvClient _client;
    private readonly string _key;
    private readonly ConsulKvResourceOptions _options;
    private readonly Func<ConfiglueResourceContext, IConsulKvClient>? _clientSelector;
    private readonly WatchShutdown _watchShutdown = new();
    private int _disposed;

    /// <summary>Creates a resource for one Consul KV key.</summary>
    /// <param name="httpClient">
    /// A caller-owned HTTP client targeting the Consul agent. The agent address is taken from
    /// <see cref="ConsulKvResourceOptions.BaseAddress"/> when set, otherwise from the HTTP
    /// client's own base address.
    /// </param>
    /// <param name="key">The Consul key.</param>
    /// <param name="options">Key, routing, endpoint, and identity settings.</param>
    public ConsulKvResource(
        HttpClient httpClient,
        string key,
        ConsulKvResourceOptions? options = null
    )
        : this(
            CreateTransport(httpClient, options ?? new ConsulKvResourceOptions()),
            key,
            options,
            options?.ClientSelector is { } selector
                ? context =>
                    CreateTransport(
                        selector(context)
                            ?? throw new InvalidOperationException(
                                "The Consul client selector returned null."
                            ),
                        options ?? new ConsulKvResourceOptions()
                    )
                : null
        ) { }

    /// <summary>Creates a resource over an internal transport. Tests use this with fakes.</summary>
    internal ConsulKvResource(
        IConsulKvClient client,
        string key,
        ConsulKvResourceOptions? options = null
    )
        : this(client, key, options, clientSelector: null) { }

    internal ConsulKvResource(
        IConsulKvClient client,
        string key,
        ConsulKvResourceOptions? options,
        Func<ConfiglueResourceContext, IConsulKvClient>? clientSelector
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        _key = ConsulKeyNormalization.NormalizeKey(key);
        _options = options ?? new ConsulKvResourceOptions();
        ValidateTimeout(_options.BlockingWaitTimeout);
        _client = client;
        _clientSelector = clientSelector;
    }

    /// <summary>The Consul key.</summary>
    public string Key => _key;

    /// <inheritdoc />
    /// <remarks>
    /// Measured (#295): KV entries are small and already buffered by
    /// <see cref="ReadAsync(ConfiglueResourceContext,CancellationToken)"/>, so wrapping them in a
    /// pipeline adds Pipe/Stream overhead without avoiding any copy. Buffered reads stay default.
    /// </remarks>
    public bool IsPipelineReadPreferred => false;

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context)
    {
        if (_options.FixedResourceId is { } explicitId)
        {
            return explicitId;
        }

        var key = ResolveKey(context);
        var datacenter = ResolveDatacenter(context) ?? string.Empty;
        var ns = ResolveNamespace(context) ?? string.Empty;
        var partition = ResolvePartition(context) ?? string.Empty;
        return new ResourceId(
            "consul:"
                + ConsulIdentityHash.Create(
                    key,
                    datacenter,
                    ns,
                    partition,
                    context.ModelId ?? string.Empty
                )
        );
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var key = ResolveKey(context);
        var result = await GetClient(context)
            .GetAsync(key, CreateListOptions(context, waitIndex: null), cancellationToken)
            .ConfigureAwait(false);
        if (result.Entries.Count == 0)
        {
            return ResourceReadResult.NotFound(
                ConsulKeyNormalization.FormatRevision(result.ConsulIndex)
            );
        }

        var entry = result.Entries[0];
        if (entry.Value is null || entry.Value.Length == 0)
        {
            return ResourceReadResult.NotFound(
                ConsulKeyNormalization.FormatRevision(entry.ModifyIndex)
            );
        }

        return ResourceReadResult.Success(
            entry.Value,
            ConsulKeyNormalization.FormatRevision(entry.ModifyIndex)
        );
    }

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var read = await ReadAsync(context, cancellationToken).ConfigureAwait(false);
        if (read.Status != StateReadStatus.Success)
        {
            return read.Status == StateReadStatus.NotFound
                ? PipelineResourceReadResult.NotFound()
                : PipelineResourceReadResult.Unavailable();
        }

        return PipelineResourceReader.FromStream(
            new MemoryStream(read.Content.ToArray(), writable: false),
            read.Revision
        );
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var key = ResolveKey(context);
        var writeOptions = CreateWriteOptions(context);
        var client = GetClient(context);
        if (request.Condition.IsNone)
        {
            if (request.Content.IsEmpty)
            {
                var deleted = await client
                    .DeleteAsync(key, null, writeOptions, cancellationToken)
                    .ConfigureAwait(false);
                return new StateWriteResult(
                    ConsulKeyNormalization.FormatRevision(deleted.NewIndex)
                );
            }

            var written = await client
                .PutAsync(key, request.Content, null, writeOptions, cancellationToken)
                .ConfigureAwait(false);
            return new StateWriteResult(ConsulKeyNormalization.FormatRevision(written.NewIndex));
        }

        if (request.Condition.IsMustNotExist)
        {
            (bool Applied, ulong NewIndex) outcome;
            if (request.Content.IsEmpty)
            {
                outcome = await client
                    .DeleteAsync(key, 0, writeOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (!outcome.Applied)
                {
                    var current = await client
                        .GetAsync(key, CreateListOptions(context, null), cancellationToken)
                        .ConfigureAwait(false);
                    if (current.Entries.Count != 0)
                    {
                        throw CreateConflict(key);
                    }
                }

                return new StateWriteResult(
                    ConsulKeyNormalization.FormatRevision(outcome.NewIndex)
                );
            }

            outcome = await client
                .PutAsync(key, request.Content, 0, writeOptions, cancellationToken)
                .ConfigureAwait(false);
            if (!outcome.Applied)
            {
                throw CreateConflict(key);
            }

            return new StateWriteResult(ConsulKeyNormalization.FormatRevision(outcome.NewIndex));
        }

        if (!ConsulKeyNormalization.TryParseRevision(request.Condition.Revision, out var expected))
        {
            throw new StateConflictException(
                $"The Consul key '{key}' no longer matches its expected revision."
            );
        }

        if (request.Content.IsEmpty)
        {
            var deleted = await client
                .DeleteAsync(key, expected, writeOptions, cancellationToken)
                .ConfigureAwait(false);
            if (!deleted.Applied)
            {
                throw CreateConflict(key);
            }

            return new StateWriteResult(ConsulKeyNormalization.FormatRevision(deleted.NewIndex));
        }

        var put = await client
            .PutAsync(key, request.Content, expected, writeOptions, cancellationToken)
            .ConfigureAwait(false);
        if (!put.Applied)
        {
            throw CreateConflict(key);
        }

        return new StateWriteResult(ConsulKeyNormalization.FormatRevision(put.NewIndex));
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        var key = ResolveKey(context);
        var client = GetClient(context);
        var listOptions = CreateListOptions(context, ParseWaitIndex(observedRevision));
        await _watchShutdown
            .WaitAsync(
                watchCancellationToken =>
                    WaitCoreAsync(
                        client,
                        key,
                        listOptions,
                        observedRevision,
                        watchCancellationToken
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

    /// <summary>Returns a redacted description that never contains credentials.</summary>
    public override string ToString() => $"consul:{_key}";

    private static async ValueTask WaitCoreAsync(
        IConsulKvClient client,
        string key,
        ConsulKvListOptions listOptions,
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        var observed = ParseWaitIndex(observedRevision);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsulKvListResult result;
            try
            {
                result = await client
                    .GetAsync(key, listOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is HttpRequestException or TimeoutException or IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (result.ConsulIndex != observed)
            {
                return;
            }

            if (result.Entries.Count == 0)
            {
                return;
            }

            var current = ConsulKeyNormalization.FormatRevision(result.Entries[0].ModifyIndex);
            if (!string.Equals(current, observedRevision, StringComparison.Ordinal))
            {
                return;
            }

            return;
        }
    }

    private IConsulKvClient GetClient(ConfiglueResourceContext context) =>
        _clientSelector?.Invoke(context) ?? _client;

    private static IConsulKvClient CreateTransport(
        HttpClient httpClient,
        ConsulKvResourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        return new HttpConsulKvClient(httpClient, options.BaseAddress, options.Token);
    }

    private string ResolveKey(ConfiglueResourceContext context)
    {
        var key = _options.KeySelector?.Invoke(context) ?? _key;
        return ConsulKeyNormalization.NormalizeKey(key);
    }

    private string? ResolveDatacenter(ConfiglueResourceContext context) =>
        _options.DatacenterSelector?.Invoke(context) ?? _options.Datacenter;

    private string? ResolveNamespace(ConfiglueResourceContext context) =>
        _options.NamespaceSelector?.Invoke(context) ?? _options.Namespace;

    private string? ResolvePartition(ConfiglueResourceContext context) =>
        _options.PartitionSelector?.Invoke(context) ?? _options.Partition;

    private ConsulKvListOptions CreateListOptions(
        ConfiglueResourceContext context,
        ulong? waitIndex
    ) =>
        new()
        {
            Datacenter = ResolveDatacenter(context),
            Namespace = ResolveNamespace(context),
            Partition = ResolvePartition(context),
            Consistency = _options.Consistency,
            WaitIndex = waitIndex,
            WaitTimeout = waitIndex is null ? null : _options.BlockingWaitTimeout,
        };

    private ConsulKvWriteOptions CreateWriteOptions(ConfiglueResourceContext context) =>
        new()
        {
            Datacenter = ResolveDatacenter(context),
            Namespace = ResolveNamespace(context),
            Partition = ResolvePartition(context),
        };

    private static ulong? ParseWaitIndex(string? revision) =>
        ConsulKeyNormalization.TryParseRevision(revision, out var index) ? index : null;

    private static StateConflictException CreateConflict(string key) =>
        new($"The Consul key '{key}' changed after it was read.");

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "A blocking wait timeout must be positive."
            );
        }
    }
}
