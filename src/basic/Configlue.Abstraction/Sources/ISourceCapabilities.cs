namespace Configlue.Sources;

/// <summary>
/// A source implementation that supplies its related read, write, and watch capabilities as one object.
/// </summary>
/// <remarks>
/// <see cref="StateSource{T}"/> consumes the capabilities exposed here when a caller supplies only the
/// reader. Sources whose related capabilities come from the same backing implementation should implement
/// this interface instead of requiring callers to coordinate separate reader, writer, and watcher objects.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface ISourceCapabilities<T> : ISourceReader<T>
{
    /// <summary>The optional writer supplied by this source.</summary>
    ISourceWriter<T>? Writer { get; }

    /// <summary>The optional change watcher supplied by this source.</summary>
    ISourceWatcher? Watcher { get; }
}
