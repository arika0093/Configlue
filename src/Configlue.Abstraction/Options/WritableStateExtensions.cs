namespace Configlue;

/// <summary>Convenience helpers for advanced Configlue state operations.</summary>
public static class ConfiglueStateExtensions
{
    /// <summary>Gets a handle that saves patches to one source.</summary>
    public static ConfiglueSourceHandle<T> Source<T>(
        this IConfiglueSources<T> sources,
        SourceKey<T> sourceKey
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (string.IsNullOrWhiteSpace(sourceKey.Id))
        {
            throw new ArgumentException("The source key is uninitialized.", nameof(sourceKey));
        }

        return new ConfiglueSourceHandle<T>(sources, sourceKey);
    }

    /// <summary>Begins editing the current value, blocking until asynchronous sources are read.</summary>
    /// <remarks>Use <see cref="IConfiglueEditSessions{T}.OpenEditSessionAsync(System.Threading.CancellationToken)"/> from asynchronous flows.</remarks>
    public static EditSession<T> OpenEditSession<T>(this IConfiglueEditSessions<T> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        // Explicit synchronous edit convenience boundary; asynchronous callers use OpenEditSessionAsync.
        return sessions.OpenEditSessionAsync().GetAwaiter().GetResult();
    }
}
