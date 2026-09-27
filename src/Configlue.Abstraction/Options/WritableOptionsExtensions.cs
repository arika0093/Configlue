namespace Configlue;

/// <summary>Compatibility helpers for writable options.</summary>
public static class WritableOptionsExtensions
{
    /// <summary>Begins editing the current value, blocking until asynchronous sources are read.</summary>
    /// <remarks>Use <see cref="IWritableOptions{T}.BeginConfigureAsync(System.Threading.CancellationToken)"/> from asynchronous flows.</remarks>
    public static ConfigureSession<T> BeginConfigure<T>(this IWritableOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.BeginConfigureAsync().GetAwaiter().GetResult();
    }
}
