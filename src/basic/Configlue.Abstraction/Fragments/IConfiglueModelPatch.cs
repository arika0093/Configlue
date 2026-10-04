namespace Configlue;

/// <summary>Whole-model operations on a generated typed patch.</summary>
/// <remarks>Advanced vocabulary: implemented by generated code, passed to state write APIs.</remarks>
/// <typeparam name="TModel">The model represented by the patch.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueModelPatch<in TModel> : IConfigluePatch
{
    /// <summary>Replaces the model contribution with the supplied value.</summary>
    void Set(TModel value);

    /// <summary>Replaces the model contribution with a present null value.</summary>
    void SetNull();

    /// <summary>Removes the model contribution.</summary>
    void Unset();
}
