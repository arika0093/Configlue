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
/// Lifetime and buffering: the returned <see cref="Resources.PipelineResourceReadResult"/> owns its
/// pipe; the serialized pipeline disposes it after consuming content to the end. Implementations
/// must return a reader positioned at the start of the payload and must not require the caller to
/// complete the pipe. Revisions may be finalized while the content is consumed.
/// </para>
/// </remarks>
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
