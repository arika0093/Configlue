namespace Configlue;

/// <summary>Compatibility helpers for writable options.</summary>
public static class WritableOptionsExtensions
{
    /// <summary>Gets a handle that saves patches to one source.</summary>
    public static ConfiglueSourceHandle<T> Source<T>(
        this IWritableOptions<T> options,
        SourceKey<T> sourceKey
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(sourceKey.Id))
        {
            throw new ArgumentException("The source key is uninitialized.", nameof(sourceKey));
        }

        return new ConfiglueSourceHandle<T>(options, sourceKey);
    }

    /// <summary>Begins editing the current value, blocking until asynchronous sources are read.</summary>
    /// <remarks>Use <see cref="IWritableOptions{T}.OpenEditSessionAsync(System.Threading.CancellationToken)"/> from asynchronous flows.</remarks>
    public static EditSession<T> OpenEditSession<T>(this IWritableOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.OpenEditSessionAsync().GetAwaiter().GetResult();
    }
}
