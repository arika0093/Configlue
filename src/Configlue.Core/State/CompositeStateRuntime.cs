namespace Configlue;

/// <summary>Exposes the composed read, write, and watch capabilities for a source set.</summary>
public sealed class CompositeStateRuntime<T>
{
    /// <summary>Creates the runtime for a logical state.</summary>
    public CompositeStateRuntime(StateSourceSet<T> sourceSet, StateWriteRoute writeRoute = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        Reader = new StateSourceResolver<T>(sourceSet);
        Writer = new StateSourceWriter<T>(sourceSet, writeRoute);
        Watcher = new StateSourceWatcher<T>(Reader);
    }

    /// <summary>The source-resolving reader.</summary>
    public StateSourceResolver<T> Reader { get; }

    /// <summary>The independently routed writer.</summary>
    public IStateWriter<T> Writer { get; }

    /// <summary>The failover and failback watcher.</summary>
    public IStateWatcher Watcher { get; }
}
