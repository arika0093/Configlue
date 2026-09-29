namespace Configlue;

/// <summary>Reads the current configuration value assembled from its state sources.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface IReadOnlyState<T>
{
    /// <summary>Subscribes to resolved values when watched state sources change.</summary>
    IDisposable OnChange(Action<T> listener);

    /// <summary>Gets the current value, throwing when no usable state can be read.</summary>
    ValueTask<T> GetValueAsync(CancellationToken cancellationToken = default);
}
