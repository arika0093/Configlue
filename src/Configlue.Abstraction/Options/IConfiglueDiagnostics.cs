namespace Configlue;

/// <summary>Reports source topology and background reload failures.</summary>
/// <typeparam name="T">The configuration model.</typeparam>
// The model parameter identifies the diagnostics service in typed and keyed DI registrations.
#pragma warning disable S2326
public interface IConfiglueDiagnostics<T>
{
    /// <summary>Subscribes to failures while the background watcher reads changed state.</summary>
    /// <remarks>Receives thrown watcher/reload exceptions and an <see cref="InvalidOperationException"/> when a changed state resolves to a non-success status. Failures from explicit read calls and change listeners are not reported here.</remarks>
    IDisposable OnReloadFailed(Action<Exception> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        throw new NotSupportedException(
            "This state implementation does not support reload-failure notifications."
        );
    }

    /// <summary>Returns the configured source topology and registration-level write routing.</summary>
    ConfiglueStateDiagnostics GetDiagnostics();
}
#pragma warning restore S2326
