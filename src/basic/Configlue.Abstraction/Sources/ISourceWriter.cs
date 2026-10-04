using Configlue.Resources;
using Configlue.State;

namespace Configlue.Sources;

/// <summary>Writes state to a backend that supports updates.</summary>
/// <remarks>Provider SPI: implemented by source authors, consumed by the runtime.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface ISourceWriter<T>
{
    /// <summary>Writes state for one model, subject, source-specific key, and route.</summary>
    ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    );
}
