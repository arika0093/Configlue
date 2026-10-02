using Configlue.Extensions.ComponentModel;

namespace Configlue.Hosting.Godot;

/// <summary>Dispatches shared ComponentModel updates through Godot's deferred callable queue.</summary>
/// <remarks>Create and use this adapter while the engine is running. Dispose state readers and contexts before engine shutdown.</remarks>
public sealed class GodotConfiglueDispatcher : IConfiglueDispatcher
{
    /// <inheritdoc />
    public bool CheckAccess() =>
        global::Godot.OS.GetThreadCallerId() == global::Godot.OS.GetMainThreadId();

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        global::Godot.Callable.From(action).CallDeferred();
    }

    /// <inheritdoc />
    public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
        ConfiglueDispatcher.InvokeAsync(this, action, cancellationToken);
}
