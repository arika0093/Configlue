using Configlue.Resources;

namespace Configlue.State;

/// <summary>Optionally reads resource content through a segmented pipeline.</summary>
/// <remarks>
/// This is an advanced performance contract, not part of the canonical provider composition path
/// (see <see cref="Extensibility.SerializedSource{T}"/>). A resource works fully without it by
/// returning buffered memory from <see cref="Resources.IResourceReader.ReadAsync"/>. Implement it
/// only when segmented reads avoid materializing large payloads (for example file, object-store,
/// or streaming resources where buffering would add large-object-heap pressure).
/// <para>
/// Measured (#295): 1/4/16/64 KiB payloads show no consistent win (Pipe/Stream overhead
/// dominates); only large blobs (hundreds of KiB/MiB) paired with a streaming-capable codec
/// justify this path. Wrapping already-buffered bytes (secrets, KV entries, section slices)
/// never avoids a copy: keep <see cref="IsPipelineReadPreferred"/> false there. Transformers,
/// schema migration, and backup recovery always force buffering, and the serialized pipeline
/// only enters this path when the codec opts in via
/// <see cref="IPipelineStateCodec{T}.IsPipelineDecodePreferred"/>.
/// </para>
/// <para>
/// Concrete third-party use case: a provider that streams large objects straight from the
/// transport (for example an S3/Azure Blob/GCS object client exposing a response stream, or a
/// file handle) without an intermediate full-payload buffer, combined with a codec that decodes
/// incrementally from the pipe. Small or already-buffered providers must not implement this.
/// </para>
/// <para>
/// Ownership (simplified): the returned <see cref="Resources.PipelineResourceReadResult"/> owns its
/// pipe; the serialized pipeline consumes it to the end and disposes it. Implementations return a
/// reader positioned at the start of the payload and never require the caller to complete the
/// pipe. Revisions may be finalized while the content is consumed.
/// </para>
/// </remarks>
/// <remarks>Advanced performance SPI; class-level hiding keeps it out of ordinary completion.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IPipelineResourceReader
{
    /// <summary>Whether the pipeline path is preferred over the existing memory-based read.</summary>
    bool IsPipelineReadPreferred { get; }

    /// <summary>Reads the resource for one model, subject, source-specific key, and route.</summary>
    ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    );
}
