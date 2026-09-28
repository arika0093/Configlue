namespace Configlue;

/// <summary>Wraps a typed state reader and writer with application-specific behavior.</summary>
/// <typeparam name="T">The state value type.</typeparam>
public interface IStateMiddleware<T>
{
    /// <summary>Wraps the next reader in the state pipeline.</summary>
    IStateReader<T> WrapReader(IStateReader<T> next);

    /// <summary>Wraps the next writer in the state pipeline.</summary>
    IStateWriter<T> WrapWriter(IStateWriter<T> next);
}
