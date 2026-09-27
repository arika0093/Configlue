using System.Buffers;

namespace Configlue;

/// <summary>Reads a typed state value by composing a resource and a codec.</summary>
public sealed class SerializedStateReader<T> : IStateReader<T>
{
    private readonly IResourceReader _resource;
    private readonly object _codec;
    private readonly StateCodecContext _context;
    private readonly StateSchemaDispatcher<T>? _schemaDispatcher;

    /// <summary>Creates a serialized state reader.</summary>
    public SerializedStateReader(
        IResourceReader resource,
        object codec,
        StateCodecContext context = default,
        StateSchemaDispatcher<T>? schemaDispatcher = null
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
    }

    /// <inheritdoc />
    public async ValueTask<StateReadResult<T>> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        var result = await _resource.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (_resource is not FileResource file || !file.AutomaticBackupRecoveryEnabled)
        {
            return Deserialize(result);
        }

        if (result.Status == StateReadStatus.NotFound)
        {
            var recovered = await TryRecoverAsync(
                    file,
                    result,
                    expectedMissing: true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return recovered is { } restored ? Deserialize(restored) : Deserialize(result);
        }

        if (result.Status == StateReadStatus.Success)
        {
            try
            {
                return Deserialize(result);
            }
            catch (Exception exception) when (IsRecoverableReadException(exception))
            {
                var recovered = await TryRecoverAsync(
                        file,
                        result,
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

        return Deserialize(result);
    }

    private async ValueTask<ResourceReadResult?> TryRecoverAsync(
        FileResource file,
        ResourceReadResult observed,
        bool expectedMissing,
        CancellationToken cancellationToken
    ) =>
        await file.TryRestoreLatestBackupAsync(
                observed.Revision,
                expectedMissing,
                (content, _) =>
                {
                    try
                    {
                        Deserialize(ResourceReadResult.Success(content, observed.Revision));
                        return ValueTask.FromResult(true);
                    }
                    catch (Exception exception) when (IsRecoverableReadException(exception))
                    {
                        return ValueTask.FromResult(false);
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);

    private bool IsRecoverableReadException(Exception exception) =>
        _codec is IStateCodecRecoveryPolicy recoveryPolicy
        && recoveryPolicy.IsRecoverableReadException(exception);

    private StateReadResult<T> Deserialize(ResourceReadResult result)
    {
        if (result.Status != StateReadStatus.Success)
        {
            return new StateReadResult<T>(
                result.Status,
                default,
                result.Revision,
                Schema: result.Schema
            );
        }

        var bytes = new ReadOnlySequence<byte>(result.Content);
        var schema =
            result.Schema
            ?? (
                _codec is IStateSchemaMetadataReader metadataReader
                    ? metadataReader.ReadSchemaMetadata(in bytes)
                    : null
            )
            ?? _context.Schema;
        var context = schema is { } metadata
            ? new StateCodecContext(metadata, _context.Services)
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

        return StateReadResult<T>.Success(value, result.Revision, schema);
    }
}
