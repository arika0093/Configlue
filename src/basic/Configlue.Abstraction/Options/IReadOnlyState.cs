namespace Configlue;

/// <summary>Reads the current configuration value assembled from its state sources.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface IReadOnlyState<T>
{
    /// <summary>Subscribes to notifications when the resolved effective value actually changes.</summary>
    /// <remarks>
    /// Listeners do not fire for source reloads whose changed contribution is shadowed and therefore
    /// leaves the effective value unchanged. Use the diagnostics surface to observe those reloads.
    /// </remarks>
    IDisposable OnChange(Action<T> listener);

    /// <summary>Gets the current value, throwing when no usable state can be read.</summary>
    ValueTask<T> GetValueAsync(CancellationToken cancellationToken = default);
}
