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

/// <summary>Reads a pipeline-backed resource using a source-specific subject key.</summary>
public interface IContextualPipelineResourceReader : IPipelineResourceReader
{
    /// <summary>Reads a subject-specific resource into a pipeline-backed result.</summary>
    ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Provides a portable fallback for contextual pipeline resource reads.</summary>
public static class PipelineResourceReaderExtensions
{
    /// <summary>Reads a pipeline-backed resource for one logical subject.</summary>
    public static ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        this IPipelineResourceReader reader,
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader is IContextualPipelineResourceReader contextualReader
            ? contextualReader.ReadPipelineAsync(context, cancellationToken)
            : reader.ReadPipelineAsync(cancellationToken);
    }
}
