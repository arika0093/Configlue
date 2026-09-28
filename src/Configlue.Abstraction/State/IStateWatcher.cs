namespace Configlue.State;

/// <summary>Waits for an upstream invalidation signal.</summary>
public interface IStateWatcher
{
    /// <summary>Waits until the source may have changed. The caller must read again for the new value.</summary>
    ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    );
}
