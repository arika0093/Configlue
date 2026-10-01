using Configlue.Resources;
using Configlue.State;

namespace Configlue.Sources;

/// <summary>Writes state to a backend that supports updates.</summary>
public interface ISourceWriter<T>
{
    /// <summary>Writes state for one model, subject, source-specific key, and route.</summary>
    ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    );
}
