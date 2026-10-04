using Configlue.Resources;
using Configlue.State;

namespace Configlue.Sources;

/// <summary>Reads the current logical state from a backend.</summary>
/// <remarks>Provider SPI: implemented by source authors, consumed by the runtime.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface ISourceReader<T>
{
    /// <summary>Reads the current state for one model, subject, source-specific key, and route.</summary>
    ValueTask<StateReadResult<T>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    );
}
