using Configlue.Resources;

namespace Configlue.State;

/// <summary>Optionally reads resource content through a segmented pipeline.</summary>
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
