namespace Configlue;

/// <summary>Implemented by source-generated models that support deep cloning.</summary>
/// <remarks>Advanced cloning contract implemented by generated code.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueDeepCloneable<out T>
{
    /// <summary>
    /// Creates an independent deep clone, preserving graph aliases and cycles.
    /// Members marked with <see cref="ConfiglueCloneReferenceSafeAttribute"/> retain their original references.
    /// </summary>
    T DeepClone();
}
