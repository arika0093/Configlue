using System.IO.Pipelines;

namespace Configlue.State;

/// <summary>Optionally decodes state directly from a pipeline-backed resource.</summary>
/// <remarks>
/// This is an advanced performance contract, not part of the canonical provider composition path
/// (see <see cref="Extensibility.SerializedSource{T}"/>). A codec works fully without it via the
/// sequence-based <see cref="Codecs.IStateCodec{T}"/> methods. Implement it only when incremental
/// decoding avoids buffering large payloads. The pipeline content is owned by the resource result:
/// the implementation must consume or drain the supplied reader without completing the pipe, and
/// must observe the cancellation token passed to the decode method.
/// </remarks>
/// <typeparam name="T">The state value type.</typeparam>
public interface IPipelineStateCodec<T>
{
    /// <summary>Whether this decoding path should be used instead of sequence-based decoding.</summary>
    bool IsPipelineDecodePreferred { get; }

    /// <summary>Decodes a value from the supplied resource pipeline.</summary>
    ValueTask<StateReadResult<T>> DeserializeAsync(
        PipeReader content,
        StateCodecContext context,
        StateSchemaMetadata? resourceSchema,
        CancellationToken cancellationToken = default
    );
}
