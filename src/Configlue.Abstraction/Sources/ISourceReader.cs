using Configlue.Resources;
using Configlue.State;

namespace Configlue.Sources;

/// <summary>Reads the current logical state from a backend.</summary>
public interface ISourceReader<T>
{
    /// <summary>Reads the current state.</summary>
    ValueTask<StateReadResult<T>> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads a source-specific state for one logical subject key.</summary>
public interface IContextualSourceReader<T> : ISourceReader<T>
{
    /// <summary>Reads state for a source-specific subject key.</summary>
    ValueTask<StateReadResult<T>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    );
}
