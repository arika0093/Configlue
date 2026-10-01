namespace Configlue;

/// <summary>Releases resources registered for Configlue ownership.</summary>
/// <remarks>
/// Ownership accepts any resource implementing <see cref="IDisposable"/> or
/// <see cref="IAsyncDisposable"/>. Asynchronous disposal is preferred, so a resource
/// implementing both is released asynchronously whenever an asynchronous boundary is available.
/// </remarks>
internal static class ConfiglueOwnedResources
{
    /// <summary>Whether the resource can participate in Configlue ownership.</summary>
    public static bool IsOwnable(object resource) =>
        resource is IDisposable || resource is IAsyncDisposable;

    /// <summary>Releases an owned resource asynchronously.</summary>
    public static async ValueTask DisposeAsync(object resource)
    {
        if (resource is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (resource is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>Releases an owned resource on a synchronous boundary.</summary>
    // Synchronous construction-failure cleanup or IDisposable boundary; normal source I/O stays asynchronous.
    public static void Dispose(object resource)
    {
        if (resource is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }

        if (resource is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
