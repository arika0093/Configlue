namespace Configlue.State;

/// <summary>Optionally reads resource content through a segmented pipeline.</summary>
public interface IPipelineResourceReader
{
    /// <summary>Whether the pipeline path is preferred over the existing memory-based read.</summary>
    bool IsPipelineReadPreferred { get; }

    /// <summary>Reads the resource into a pipeline-backed result.</summary>
    ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        CancellationToken cancellationToken = default
    );
}
