using Configlue.Resources;
using Configlue.State;

namespace Configlue.Sources;

/// <summary>Writes state to a backend that supports updates.</summary>
public interface ISourceWriter<T>
{
    /// <summary>Writes state, optionally requiring the backend revision to match.</summary>
    ValueTask<StateWriteResult> WriteAsync(
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    );

    /// <summary>Writes state for a source-specific subject key.</summary>
    ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    ) => WriteAsync(request, cancellationToken);
}
