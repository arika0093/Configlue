using Configlue.Resources;
using Configlue.State;

namespace Configlue.Sources;

/// <summary>Reads the current logical state from a backend.</summary>
public interface ISourceReader<T>
{
    /// <summary>Reads the current state for one model, subject, source-specific key, and route.</summary>
    ValueTask<StateReadResult<T>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    );
}
