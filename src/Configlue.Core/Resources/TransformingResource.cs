namespace Configlue;

/// <summary>Applies byte transformers around a resource before provider-specific document processing.</summary>
public sealed class TransformingResource
    : IResourceReader,
        IResourceIdentity,
        IResourceBackupRecovery
{
    private readonly IResourceReader _reader;
    private readonly IResourceBackupRecovery? _backupRecovery;
    private readonly IStateByteTransformer[] _transformers;

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
            ?? (resource as IResourceIdentity)?.ResourceId
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

    /// <summary>A writer that transforms bytes before persisting them, if the resource is writable.</summary>
    public IResourceWriter? Writer { get; }

    /// <summary>The wrapped resource watcher, if available.</summary>
    public IStateWatcher? Watcher { get; }

    /// <inheritdoc />
    public bool AutomaticBackupRecoveryEnabled =>
        _backupRecovery?.AutomaticBackupRecoveryEnabled == true;

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return result.Status == StateReadStatus.Success
            ? result with
            {
                Content = TransformRead(result.Content),
            }
            : result;
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
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
        ) =>
            writer.WriteAsync(
                new ResourceWriteRequest(
                    owner.TransformWrite(request.Content),
                    request.ExpectedRevision,
                    request.Schema,
                    request.CheckRevision
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
                    mutation.ExpectedRevision,
                    mutation.CheckRevision,
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
                    mutation.Scope,
                    mutation.CanCompose
                );
            }

            return _batchWriter.WriteBatchAsync(transformedMutations, cancellationToken);
        }
    }
}
