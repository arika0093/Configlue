using Configlue.Resources;

namespace Configlue.Sources;

/// <summary>Waits for an upstream invalidation signal.</summary>
public interface ISourceWatcher
{
    /// <summary>Waits until the source may have changed for one model, subject, key, and route.</summary>
    ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    );
}
