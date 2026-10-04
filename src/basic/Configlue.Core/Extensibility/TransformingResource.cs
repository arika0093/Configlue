using Configlue.Sources;

namespace Configlue.Extensibility;

/// <summary>Applies byte transformers around a resource before provider-specific document processing.</summary>
/// <remarks>Provider SPI composition helper: wraps one resource with transformers.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class TransformingResource
    : IResourceReader,
        ITryResourceIdentity,
        IContextualResourceBackupRecovery
{
    private readonly IResourceReader _reader;
    private readonly IResourceBackupRecovery? _backupRecovery;
    private readonly IStateByteTransformer[] _transformers;
    private readonly ResourceId? _configuredResourceId;

    /// <summary>Creates a resource view that transforms the raw resource bytes.</summary>
    public TransformingResource(
        IResourceReader resource,
        IEnumerable<IStateByteTransformer> transformers,
        ResourceId? fixedResourceId = null
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(transformers);
        _reader = resource;
        _backupRecovery = resource as IResourceBackupRecovery;
        _configuredResourceId = fixedResourceId;
        _transformers = transformers.ToArray();
        if (_transformers.Any(static transformer => transformer is null))
        {
            throw new ArgumentException(
                "A transformer collection cannot contain null values.",
                nameof(transformers)
            );
        }
        var invalid = _transformers.FirstOrDefault(static transformer =>
            transformer is not ISynchronousStateByteTransformer
            && transformer is not IAsyncStateByteTransformer
        );
        if (invalid is not null)
        {
            throw new ArgumentException(
                $"Transformer type '{invalid.GetType()}' must implement a synchronous or asynchronous transformer capability.",
                nameof(transformers)
            );
        }

        if (resource is IResourceWriter writer)
        {
            Writer =
                resource is IResourceBatchWriter batchWriter
                && !_transformers.Any(static transformer =>
                    transformer is IAsyncStateByteTransformer
                )
                    ? new TransformingBatchWriter(this, writer, batchWriter)
                    : new TransformingWriter(this, writer);
        }
        Watcher = resource as ISourceWatcher;
    }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        TryGetResourceId(context, out var resourceId)
            ? resourceId
            : throw new InvalidOperationException("The wrapped resource has no physical identity.");

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        if (_configuredResourceId is { } configuredResourceId)
        {
            resourceId = configuredResourceId;
            return true;
        }

        return _reader.TryGetResourceId(context, out resourceId);
    }

    /// <summary>A writer that transforms bytes before persisting them, if the resource is writable.</summary>
    public IResourceWriter? Writer { get; }

    /// <summary>The wrapped resource watcher, if available.</summary>
    public ISourceWatcher? Watcher { get; }

    /// <inheritdoc />
    public bool AutomaticBackupRecoveryEnabled =>
        _backupRecovery?.AutomaticBackupRecoveryEnabled == true;

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var result = await _reader.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        if (result.Status != StateReadStatus.Success)
            return result;
        return ResourceReadResult.Success(
            await TransformReadAsync(result.Content, cancellationToken).ConfigureAwait(false),
            result.Revision,
            result.Schema
        );
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
                            ? ResourceReadResult.Success(
                                await TransformReadAsync(candidate.Content, token)
                                    .ConfigureAwait(false),
                                candidate.Revision,
                                candidate.Schema
                            )
                            : candidate;
                    return await validate(decoded, token).ConfigureAwait(false);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return restored is { } result && result.Status == StateReadStatus.Success
            ? ResourceReadResult.Success(
                await TransformReadAsync(result.Content, cancellationToken).ConfigureAwait(false),
                result.Revision,
                result.Schema
            )
            : restored;
    }

    private ValueTask<ReadOnlyMemory<byte>> TransformReadAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken
    ) => StateByteTransformerPipeline.TransformReadAsync(content, _transformers, cancellationToken);

    private ValueTask<ReadOnlyMemory<byte>> TransformWriteAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken
    ) =>
        StateByteTransformerPipeline.TransformWriteAsync(content, _transformers, cancellationToken);

    private ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> content) =>
        StateByteTransformerPipeline.TransformRead(content, _transformers);

    private ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> content) =>
        StateByteTransformerPipeline.TransformWrite(content, _transformers);

    private class TransformingWriter(TransformingResource owner, IResourceWriter writer)
        : IResourceWriter
    {
        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            ResourceWriteRequest request,
            CancellationToken cancellationToken = default
        ) => WriteCoreAsync(context, request, cancellationToken);

        private async ValueTask<StateWriteResult> WriteCoreAsync(
            ConfiglueResourceContext context,
            ResourceWriteRequest request,
            CancellationToken cancellationToken
        )
        {
            var content = await owner
                .TransformWriteAsync(request.Content, cancellationToken)
                .ConfigureAwait(false);
            return await writer
                .WriteAsync(
                    context,
                    new ResourceWriteRequest(
                        content,
                        Condition: request.Condition,
                        Schema: request.Schema
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private sealed class TransformingBatchWriter
        : TransformingWriter,
            IResourceBatchWriter,
            ITryResourceIdentity
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

        public ResourceId GetResourceId(ConfiglueResourceContext context) =>
            _owner.GetResourceId(context);

        public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId) =>
            _owner.TryGetResourceId(context, out resourceId);

        public ValueTask<StateWriteResult> WriteBatchAsync(
            IReadOnlyList<ResourceWriteMutation> mutations,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(mutations);
            var transformedMutations = new ResourceWriteMutation[mutations.Count];
            var transformRead = _owner.TransformRead;
            var transformWrite = _owner.TransformWrite;
            for (var index = 0; index < mutations.Count; index++)
            {
                transformedMutations[index] = mutations[index]
                    .WithTransforms(transformRead, transformWrite);
            }

            return _batchWriter.WriteBatchAsync(transformedMutations, cancellationToken);
        }
    }
}
