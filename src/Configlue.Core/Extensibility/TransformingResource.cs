namespace Configlue.Extensibility;

/// <summary>Applies byte transformers around a resource before provider-specific document processing.</summary>
public sealed class TransformingResource
    : IResourceReader,
        IResourceIdentity,
        IResourceBackupRecovery
{
    private readonly IResourceReader _reader;
    private readonly IResourceBackupRecovery? _backupRecovery;
    private readonly IStateByteTransformer[] _transformers;
    private readonly ResourceId? _configuredResourceId;

    /// <summary>Creates a resource view that transforms the raw resource bytes.</summary>
    public TransformingResource(
        IResourceReader resource,
        IEnumerable<IStateByteTransformer> transformers,
        ResourceId? resourceId = null
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(transformers);
        _reader = resource;
        _backupRecovery = resource as IResourceBackupRecovery;
        _configuredResourceId = resourceId;
        _transformers = transformers.ToArray();
        if (_transformers.Any(static transformer => transformer is null))
        {
            throw new ArgumentException(
                "A transformer collection cannot contain null values.",
                nameof(transformers)
            );
        }

        ResourceId =
            resourceId
            ?? (
                resource is IResourceIdentity identity
                && identity.TryGetResourceId(ConfiglueResourceContext.Default, out var resolvedId)
                    ? (ResourceId?)resolvedId
                    : null
            )
            ?? throw new ArgumentException(
                "A transforming resource requires a physical resource identity.",
                nameof(resource)
            );
        if (resource is IResourceWriter writer)
        {
            Writer = resource is IResourceBatchWriter batchWriter
                ? new TransformingBatchWriter(this, writer, batchWriter)
                : new TransformingWriter(this, writer);
        }
        Watcher = resource as IStateWatcher;
    }

    /// <summary>The physical identity of the wrapped resource.</summary>
    public ResourceId ResourceId { get; }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        TryGetResourceId(context, out var resourceId) ? resourceId : ResourceId;

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        if (_configuredResourceId is { } configuredResourceId)
        {
            resourceId = configuredResourceId;
            return true;
        }

        if (_reader is IResourceIdentity identity)
        {
            return identity.TryGetResourceId(context, out resourceId);
        }

        resourceId = ResourceId;
        return true;
    }

    /// <summary>A writer that transforms bytes before persisting them, if the resource is writable.</summary>
    public IResourceWriter? Writer { get; }

    /// <summary>The wrapped resource watcher, if available.</summary>
    public IStateWatcher? Watcher { get; }

    /// <inheritdoc />
    public bool AutomaticBackupRecoveryEnabled =>
        _backupRecovery?.AutomaticBackupRecoveryEnabled == true;

    /// <inheritdoc />
    public ValueTask<ResourceReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(ConfiglueResourceContext.Default, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var result = await _reader.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        return result.Status == StateReadStatus.Success
            ? result with
            {
                Content = TransformRead(result.Content),
            }
            : result;
    }

    /// <inheritdoc />
    public ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
        string? expectedRevision,
        bool expectedMissing,
        Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
        CancellationToken cancellationToken = default
    ) =>
        TryRecoverLatestBackupAsync(
            ConfiglueResourceContext.Default,
            expectedRevision,
            expectedMissing,
            validate,
            cancellationToken
        );

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
        ConfiglueResourceContext context,
        string? expectedRevision,
        bool expectedMissing,
        Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(validate);
        if (_backupRecovery is null || !AutomaticBackupRecoveryEnabled)
        {
            return null;
        }

        var restored = await _backupRecovery
            .TryRecoverLatestBackupAsync(
                context,
                expectedRevision,
                expectedMissing,
                async (candidate, token) =>
                {
                    var decoded =
                        candidate.Status == StateReadStatus.Success
                            ? candidate with
                            {
                                Content = TransformRead(candidate.Content),
                            }
                            : candidate;
                    return await validate(decoded, token).ConfigureAwait(false);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return restored is { } result && result.Status == StateReadStatus.Success
            ? result with
            {
                Content = TransformRead(result.Content),
            }
            : restored;
    }

    private ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> content) =>
        StateByteTransformerPipeline.TransformRead(content, _transformers);

    private ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> content) =>
        StateByteTransformerPipeline.TransformWrite(content, _transformers);

    private class TransformingWriter(TransformingResource owner, IResourceWriter writer)
        : IResourceWriter
    {
        public ValueTask<StateWriteResult> WriteAsync(
            ResourceWriteRequest request,
            CancellationToken cancellationToken = default
        ) => WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            ResourceWriteRequest request,
            CancellationToken cancellationToken = default
        ) =>
            writer.WriteAsync(
                context,
                new ResourceWriteRequest(
                    owner.TransformWrite(request.Content),
                    Condition: request.Condition,
                    Schema: request.Schema
                ),
                cancellationToken
            );
    }

    private sealed class TransformingBatchWriter : TransformingWriter, IResourceBatchWriter
    {
        private readonly TransformingResource _owner;
        private readonly IResourceBatchWriter _batchWriter;

        public TransformingBatchWriter(
            TransformingResource owner,
            IResourceWriter writer,
            IResourceBatchWriter batchWriter
        )
            : base(owner, writer)
        {
            _owner = owner;
            _batchWriter = batchWriter;
        }

        public ResourceId ResourceId => _owner.ResourceId;

        public ResourceId GetResourceId(ConfiglueResourceContext context) =>
            _owner.GetResourceId(context);

        public ValueTask<StateWriteResult> WriteBatchAsync(
            IReadOnlyList<ResourceWriteMutation> mutations,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(mutations);
            var transformedMutations = new ResourceWriteMutation[mutations.Count];
            for (var index = 0; index < mutations.Count; index++)
            {
                var mutation = mutations[index];
                transformedMutations[index] = new ResourceWriteMutation(
                    mutation.Condition,
                    mutation.Schema,
                    current =>
                    {
                        var decoded =
                            current.Status == StateReadStatus.Success
                                ? current with
                                {
                                    Content = _owner.TransformRead(current.Content),
                                }
                                : current;
                        return _owner.TransformWrite(mutation.Apply(decoded));
                    },
                    scope: mutation.Scope,
                    canCompose: mutation.CanCompose,
                    context: mutation.Context
                );
            }

            return _batchWriter.WriteBatchAsync(transformedMutations, cancellationToken);
        }
    }
}
