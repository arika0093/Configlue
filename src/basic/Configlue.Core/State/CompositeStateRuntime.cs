using Configlue.Sources;

namespace Configlue.State;

/// <summary>Exposes the composed read, write, and watch capabilities for a source set.</summary>
public sealed class CompositeStateRuntime<T>
{
    /// <summary>Creates the runtime for a logical state.</summary>
    public CompositeStateRuntime(StateSourceSet<T> sourceSet, SourceId? defaultWriteSourceId = null)
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        Reader = new StateSourceResolver<T>(sourceSet);
        Writer = new StateSourceWriter<T>(sourceSet, defaultWriteSourceId);
        Watcher = new StateSourceWatcher<T>(Reader);
    }

    /// <summary>The source-resolving reader.</summary>
    public StateSourceResolver<T> Reader { get; }

    /// <summary>The independently routed writer.</summary>
    public ISourceWriter<T> Writer { get; }

    /// <summary>The failover and failback watcher.</summary>
    public ISourceWatcher Watcher { get; }
}
