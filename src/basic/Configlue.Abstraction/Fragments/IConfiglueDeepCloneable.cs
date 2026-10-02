namespace Configlue;

/// <summary>Implemented by source-generated models that support deep cloning.</summary>
public interface IConfiglueDeepCloneable<out T>
{
    /// <summary>
    /// Creates an independent deep clone, preserving graph aliases and cycles.
    /// Members marked with <see cref="ConfiglueCloneReferenceSafeAttribute"/> retain their original references.
    /// </summary>
    T DeepClone();
}
