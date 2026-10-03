using Configlue.Sources;

namespace Configlue.State;

/// <summary>Wraps a typed state reader with application-specific behavior.</summary>
/// <typeparam name="T">The state value type.</typeparam>
public interface IStateReaderMiddleware<T>
{
    /// <summary>Wraps the next reader in the state pipeline.</summary>
    ISourceReader<T> WrapReader(ISourceReader<T> next);
}

/// <summary>Wraps a typed state writer with application-specific behavior.</summary>
/// <typeparam name="T">The state value type.</typeparam>
public interface IStateWriterMiddleware<T>
{
    /// <summary>Wraps the next writer in the state pipeline.</summary>
    ISourceWriter<T> WrapWriter(ISourceWriter<T> next);
}
