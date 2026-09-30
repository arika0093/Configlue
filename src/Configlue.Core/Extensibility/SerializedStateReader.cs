using System.Buffers;
using System.IO.Pipelines;
using Configlue.Sources;

namespace Configlue.Extensibility;

/// <summary>Reads a typed state value by composing a resource and a codec.</summary>
public sealed class SerializedStateReader<T>
    : IContextualSourceReader<T>,
        ITryContextualResourceIdentity
{
    private readonly IResourceReader _resource;
    private readonly object _codec;
    private readonly StateCodecContext _context;
    private readonly StateSchemaDispatcher<T>? _schemaDispatcher;
    private readonly IStateByteTransformer[] _transformers;

    /// <summary>Creates a serialized state reader.</summary>
    public SerializedStateReader(
        IResourceReader resource,
        object codec,
        StateCodecContext context = default,
        StateSchemaDispatcher<T>? schemaDispatcher = null,
        IEnumerable<IStateByteTransformer>? transformers = null
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(codec);
        if (codec is not IStateCodec<T> && codec is not IStateCodec)
        {
            throw new ArgumentException(
                "The codec must implement IStateCodec or IStateCodec<T>.",
                nameof(codec)
            );
        }

        _resource = resource;
        _codec = codec;
        _context = context;
        _schemaDispatcher = schemaDispatcher;
        _transformers = StateByteTransformerPipeline.Create(transformers);
    }

    /// <inheritdoc />
    public ResourceId ResourceId => GetResourceId(ConfiglueResourceContext.Default);

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        TryGetResourceId(context, out var resourceId)
            ? resourceId
            : throw new InvalidOperationException(
                "The underlying resource has no physical identity."
            );

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        if (_resource is IResourceIdentity identity)
        {
            return identity.TryGetResourceId(context, out resourceId);
        }

        resourceId = default;
        return false;
    }

    /// <inheritdoc />
    public async ValueTask<StateReadResult<T>> ReadAsync(
        CancellationToken cancellationToken = default
    ) => await ReadAsync(ConfiglueResourceContext.Default, cancellationToken).ConfigureAwait(false);

    /// <summary>Reads and deserializes state for one logical subject and source-specific key.</summary>
    public async ValueTask<StateReadResult<T>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (
            _schemaDispatcher is null
            && _transformers.Length == 0
            && _resource is IPipelineResourceReader { IsPipelineReadPreferred: true } pipelineReader
            && _resource is not IResourceBackupRecovery { AutomaticBackupRecoveryEnabled: true }
        )
        {
            try
            {
                return await ReadPipelineAsync(pipelineReader, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                return StateReadResult<T>.Unavailable();
            }
        }

        ResourceReadResult result;
        try
        {
            result = await _resource.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableReadException(exception))
        {
            if (
                _resource is IResourceBackupRecovery recovery
                && recovery.AutomaticBackupRecoveryEnabled
                && TryGetObservedRevision(exception, out var observedRevision)
            )
            {
                var recovered = await TryRecoverAsync(
                        recovery,
                        context,
                        observedRevision,
                        expectedMissing: false,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (recovered is { } restored)
                {
                    return Deserialize(restored);
                }
            }

            throw;
        }

        if (
            _resource is not IResourceBackupRecovery backupRecovery
            || !backupRecovery.AutomaticBackupRecoveryEnabled
        )
        {
            return Deserialize(result);
        }

        if (result.Status == StateReadStatus.NotFound)
        {
            if (result.Revision is not null)
            {
                return Deserialize(result);
            }

            var recovered = await TryRecoverAsync(
                    backupRecovery,
                    context,
                    result.Revision,
                    expectedMissing: true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return recovered is { } restored ? Deserialize(restored) : Deserialize(result);
        }

        try
        {
            return Deserialize(result);
        }
        catch (Exception exception) when (IsRecoverableReadException(exception))
        {
            var recovered = await TryRecoverAsync(
                    backupRecovery,
                    context,
                    result.Revision,
                    expectedMissing: false,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (recovered is { } restored)
            {
                return Deserialize(restored);
            }

            throw;
        }
    }

    private async ValueTask<ResourceReadResult?> TryRecoverAsync(
        IResourceBackupRecovery resource,
        ConfiglueResourceContext resourceContext,
        string? observedRevision,
        bool expectedMissing,
        CancellationToken cancellationToken
    ) =>
        await resource
            .TryRecoverLatestBackupAsync(
                resourceContext,
                observedRevision,
                expectedMissing,
                (candidate, _) =>
                {
                    if (candidate.Status != StateReadStatus.Success)
                    {
                        return new ValueTask<bool>(false);
                    }

                    try
                    {
                        Deserialize(candidate);
                        return new ValueTask<bool>(true);
                    }
                    catch (Exception exception) when (IsRecoverableReadException(exception))
                    {
                        return new ValueTask<bool>(false);
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);

    private static bool TryGetObservedRevision(Exception exception, out string? revision)
    {
        if (exception.Data["Configlue.ObservedResourceRevision"] is string observedRevision)
        {
            revision = observedRevision;
            return true;
        }

        revision = null;
        return false;
    }

    private bool IsRecoverableReadException(Exception exception) =>
        (
            _codec is IStateCodecRecoveryPolicy recoveryPolicy
            && recoveryPolicy.IsRecoverableReadException(exception)
        )
        || _transformers.Any(transformer =>
            transformer is IStateByteTransformerRecoveryPolicy recoveryPolicy
            && recoveryPolicy.IsRecoverableReadException(exception)
        );

    private StateReadResult<T> Deserialize(ResourceReadResult result)
    {
        if (result.Status != StateReadStatus.Success)
        {
            return StateReadResult<T>.Create(
                result.Status,
                default,
                result.Revision,
                Schema: result.Schema
            );
        }

        var content = StateByteTransformerPipeline.TransformRead(result.Content, _transformers);
        var bytes = new ReadOnlySequence<byte>(content);
        return DeserializeContent(in bytes, result.Revision, result.Schema);
    }

    private async ValueTask<StateReadResult<T>> ReadPipelineAsync(
        IPipelineResourceReader pipelineReader,
        ConfiglueResourceContext resourceContext,
        CancellationToken cancellationToken
    )
    {
        var result = await pipelineReader
            .ReadPipelineAsync(resourceContext, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status != StateReadStatus.Success)
        {
            return StateReadResult<T>.Create(
                result.Status,
                default,
                result.Revision,
                Schema: result.Schema
            );
        }

        await using (result.ConfigureAwait(false))
        {
            if (
                _transformers.Length == 0
                && _codec
                    is IPipelineStateCodec<T> { IsPipelineDecodePreferred: true } pipelineCodec
            )
            {
                var decoded = await pipelineCodec
                    .DeserializeAsync(result.Content!, _context, result.Schema, cancellationToken)
                    .ConfigureAwait(false);
                await result.DrainAsync(cancellationToken).ConfigureAwait(false);
                return decoded with
                {
                    Revision = result.Revision,
                    Schema = decoded.Schema ?? result.Schema ?? _context.Schema,
                };
            }

            var content = await result.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return DeserializeContent(in content, result.Revision, result.Schema);
            }
            finally
            {
                result.Content!.AdvanceTo(content.End);
            }
        }
    }

    private StateReadResult<T> DeserializeContent(
        in ReadOnlySequence<byte> bytes,
        string? revision,
        StateSchemaMetadata? resourceSchema
    )
    {
        var schema =
            resourceSchema
            ?? (
                _codec is IStateSchemaMetadataReader metadataReader
                    ? metadataReader.ReadSchemaMetadata(in bytes)
                    : null
            )
            ?? _context.Schema;
        var context = schema is { } metadata
            ? new StateCodecContext(metadata, _context.Services, _context.SchemaReferenceBaseUri)
            : _context;
        var schemaDispatcher = _schemaDispatcher;
        T? value = default;
        if (
            schema is { } sourceSchema
            && schemaDispatcher is not null
            && schemaDispatcher.TryDeserialize(sourceSchema, in bytes, _context.Services, out value)
        )
        {
            schema = schemaDispatcher.TargetSchema;
        }
        else
        {
            value = _codec switch
            {
                IStateCodec<T> typed => typed.Deserialize(in bytes, in context),
                IStateCodec untyped => (T?)untyped.Deserialize(typeof(T), in bytes, in context),
                _ => throw new InvalidOperationException(
                    "The codec does not implement a supported state codec interface."
                ),
            };
        }

        return value is null
            ? StateReadResult<T>.Invalid(default, revision) with
            {
                Schema = schema,
            }
            : StateReadResult<T>.Success(value, revision, schema);
    }
}
