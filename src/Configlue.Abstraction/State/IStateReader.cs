using Configlue.Resources;

namespace Configlue.State;

/// <summary>Reads the current logical state from a backend.</summary>
public interface IStateReader<T>
{
    /// <summary>Reads the current state.</summary>
    ValueTask<StateReadResult<T>> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads state for a source-specific subject key.</summary>
    ValueTask<StateReadResult<T>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    ) => ReadAsync(cancellationToken);
}
