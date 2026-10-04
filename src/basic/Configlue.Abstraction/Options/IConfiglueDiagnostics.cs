namespace Configlue;

/// <summary>Reports source topology, background reload failures, and operational checks.</summary>
/// <remarks>Advanced observability service.</remarks>
/// <typeparam name="T">The configuration model.</typeparam>
// The model parameter identifies the diagnostics service in typed and keyed DI registrations.
#pragma warning disable S2326
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueDiagnostics<T>
{
    /// <summary>Returns the configured source topology and registration-level write routing.</summary>
    ConfiglueStateDiagnostics GetDiagnostics();

    /// <summary>
    /// Starts one operational check that streams the sources evaluated by resolution and exposes one
    /// final state-level result.
    /// </summary>
    ConfiglueCheckOperation Check(CancellationToken cancellationToken = default);
}

/// <summary>Provides reload-failure notifications when supported by a diagnostics implementation.</summary>
/// <remarks>Advanced observability service.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueReloadFailureDiagnostics<T> : IConfiglueDiagnostics<T>
{
    /// <summary>Subscribes to failures while the background watcher reads changed state.</summary>
    IDisposable OnReloadFailed(Action<Exception> listener);
}
#pragma warning restore S2326
