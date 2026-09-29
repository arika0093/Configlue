using Configlue.Resources;

namespace Configlue.Sources;

/// <summary>Waits for an upstream invalidation signal.</summary>
public interface ISourceWatcher
{
    /// <summary>Waits until the source may have changed. The caller must read again for the new value.</summary>
    ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    );

    /// <summary>Waits for changes to one source-specific subject key.</summary>
    ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) => WaitForChangeAsync(observedRevision, cancellationToken);
}
