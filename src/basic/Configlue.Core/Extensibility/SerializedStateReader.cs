using System.Buffers;
using System.IO.Pipelines;
using Configlue.Codecs;
using Configlue.Sources;

namespace Configlue.Extensibility;

/// <summary>Reads a typed state value by composing a resource and a codec.</summary>
public sealed class SerializedStateReader<T> : ISourceReader<T>, ITryResourceIdentity
{
    private readonly IResourceReader _resource;
    private readonly StateCodecBinding _codecBinding;
    private readonly IStateCodec<T>? _typedCodec;
    private readonly IStateCodec? _dynamicCodec;
    private readonly StateCodecContext _context;
    private readonly StateSchemaDispatcher<T>? _schemaDispatcher;
    private readonly IStateByteTransformer[] _transformers;

    /// <summary>Creates a serialized state reader.</summary>
    public SerializedStateReader(
        IResourceReader resource,
        IStateCodec<T> codec,
        StateCodecContext context = default,
        StateSchemaDispatcher<T>? schemaDispatcher = null,
        IEnumerable<IStateByteTransformer>? transformers = null
    )
        : this(resource, StateCodecBinding.Typed(codec), context, schemaDispatcher, transformers)
    { }

    /// <summary>Creates a serialized state reader from an explicit typed or dynamic codec binding.</summary>
    public SerializedStateReader(
        IResourceReader resource,
        StateCodecBinding codec,
        StateCodecContext context = default,
        StateSchemaDispatcher<T>? schemaDispatcher = null,
        IEnumerable<IStateByteTransformer>? transformers = null
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(codec);
        if (!codec.TryGetTyped<T>(out _typedCodec) && codec.DynamicCodec is null)
        {
            throw new ArgumentException(
                $"The codec binding is for '{codec.StateType}', not '{typeof(T)}'.",
                nameof(codec)
            );
        }

        _resource = resource;
        _codecBinding = codec;
        _dynamicCodec = codec.DynamicCodec;
        _context = context;
        _schemaDispatcher = schemaDispatcher;
        _transformers = StateByteTransformerPipeline.Create(transformers);
    }

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        return _resource.TryGetResourceId(context, out resourceId);
    }

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
                    return await DeserializeAsync(restored, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            throw;
        }

        if (
            _resource is not IResourceBackupRecovery backupRecovery
            || !backupRecovery.AutomaticBackupRecoveryEnabled
        )
        {
            return await DeserializeAsync(result, cancellationToken).ConfigureAwait(false);
        }

        if (result.Status == StateReadStatus.NotFound)
        {
            if (result.Revision is not null)
            {
                return await DeserializeAsync(result, cancellationToken).ConfigureAwait(false);
            }

            var recovered = await TryRecoverAsync(
                    backupRecovery,
                    context,
                    result.Revision,
                    expectedMissing: true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return recovered is { } restored
                ? await DeserializeAsync(restored, cancellationToken).ConfigureAwait(false)
                : await DeserializeAsync(result, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await DeserializeAsync(result, cancellationToken).ConfigureAwait(false);
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
                return await DeserializeAsync(restored, cancellationToken).ConfigureAwait(false);
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
                async (candidate, token) =>
                {
                    if (candidate.Status != StateReadStatus.Success)
                    {
                        return false;
                    }

                    try
                    {
                        await DeserializeAsync(candidate, token).ConfigureAwait(false);
                        return true;
                    }
                    catch (Exception exception) when (IsRecoverableReadException(exception))
                    {
                        return false;
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
            _codecBinding.GetCapability<IStateCodecRecoveryPolicy>() is { } recoveryPolicy
            && recoveryPolicy.IsRecoverableReadException(exception)
        )
        || _transformers.Any(transformer =>
            transformer is IStateByteTransformerRecoveryPolicy recoveryPolicy
            && recoveryPolicy.IsRecoverableReadException(exception)
        );

    private async ValueTask<StateReadResult<T>> DeserializeAsync(
        ResourceReadResult result,
        CancellationToken cancellationToken
    )
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

        var content = await StateByteTransformerPipeline
            .TransformReadAsync(result.Content, _transformers, cancellationToken)
            .ConfigureAwait(false);
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
                && _codecBinding.GetCapability<IPipelineStateCodec<T>>()
                    is { IsPipelineDecodePreferred: true } pipelineCodec
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
        if (_schemaDispatcher is null && _typedCodec is IStateCodecWithMetadata<T> singlePassCodec)
        {
            var decodeContext = resourceSchema is { } resourceMetadata
                ? new StateCodecContext(
                    resourceMetadata,
                    _context.Services,
                    _context.SchemaReferenceBaseUri
                )
                : _context;
            var decoded = singlePassCodec.DeserializeWithMetadata(in bytes, in decodeContext);
            var decodedSchema = resourceSchema ?? decoded.Schema ?? _context.Schema;
            return decoded.Value is null
                ? StateReadResult<T>.InvalidPayload(default, revision) with
                {
                    Schema = decodedSchema,
                }
                : StateReadResult<T>.Success(decoded.Value, revision, decodedSchema);
        }

        var schema =
            resourceSchema
            ?? (
                _codecBinding.GetCapability<IStateSchemaMetadataReader>() is { } metadataReader
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
            value = _typedCodec is { } typedCodec
                ? typedCodec.Deserialize(in bytes, in context)
                : (T?)_dynamicCodec!.Deserialize(typeof(T), in bytes, in context);
        }

        return value is null
            ? StateReadResult<T>.InvalidPayload(default, revision) with
            {
                Schema = schema,
            }
            : StateReadResult<T>.Success(value, revision, schema);
    }
}
