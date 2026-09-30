using System.IO.Pipelines;

namespace Configlue.State;

/// <summary>Optionally decodes state directly from a pipeline-backed resource.</summary>
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
