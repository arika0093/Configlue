namespace Configlue;

/// <summary>Creates independent copies of resolved model values for consumer-facing caches.</summary>
/// <remarks>Advanced cloning SPI.</remarks>
/// <typeparam name="T">The configuration model type.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueValueCloneProvider<T>
{
    /// <summary>Creates a distinct model with mutable members independent of the supplied value.</summary>
    T CloneValue(T value);
}
