using System.IO.Pipelines;

namespace Configlue.State;

/// <summary>Optionally decodes state directly from a pipeline-backed resource.</summary>
/// <remarks>
/// This is an advanced performance contract, not part of the canonical provider composition path
/// (see <see cref="Extensibility.SerializedSource{T}"/>). A codec works fully without it via the
/// sequence-based <see cref="Codecs.IStateCodec{T}"/> methods. Implement it only when incremental
/// decoding avoids buffering large payloads: measured (#295) small payloads (up to 64 KiB) show no
/// win, while large payloads with <c>JsonStateCodec{T}.UseAsyncStreamDecoding</c> save roughly
/// 0.25 MiB per read at 256 KiB and 1.9 MiB at 2 MiB. The serialized pipeline only selects the
/// pipeline resource path when this capability prefers it, so leaving
/// <see cref="IsPipelineDecodePreferred"/> false keeps ordinary reads on the simple buffered path.
/// <para>
/// Ownership (simplified): the pipeline content is owned by the resource result. Consume or drain
/// the supplied reader without completing the pipe, and observe the cancellation token passed to
/// the decode method.
/// </para>
/// </remarks>
/// <remarks>Advanced performance SPI; class-level hiding keeps it out of ordinary completion.</remarks>
/// <typeparam name="T">The state value type.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
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
