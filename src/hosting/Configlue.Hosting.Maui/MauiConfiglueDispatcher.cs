using Configlue.Extensions.ComponentModel;
using Microsoft.Maui.ApplicationModel;

namespace Configlue.Hosting.Maui;

/// <summary>Dispatches shared ComponentModel updates through MAUI's native main-thread facilities.</summary>
public sealed class MauiConfiglueDispatcher : IConfiglueDispatcher
{
    /// <inheritdoc />
    public bool CheckAccess() => MainThread.IsMainThread;

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        MainThread.BeginInvokeOnMainThread(action);
    }
}
