namespace Configlue;

/// <summary>Implemented by source-generated models that support deep cloning.</summary>
public interface IConfiglueDeepCloneable<out T>
{
    /// <summary>Creates an independent deep clone.</summary>
    T DeepClone();
}
