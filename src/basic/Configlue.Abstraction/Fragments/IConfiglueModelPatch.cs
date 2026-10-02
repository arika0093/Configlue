namespace Configlue;

/// <summary>Whole-model operations on a generated typed patch.</summary>
/// <typeparam name="TModel">The model represented by the patch.</typeparam>
public interface IConfiglueModelPatch<in TModel> : IConfigluePatch
{
    /// <summary>Replaces the model contribution with the supplied value.</summary>
    void Set(TModel value);

    /// <summary>Replaces the model contribution with a present null value.</summary>
    void SetNull();

    /// <summary>Removes the model contribution.</summary>
    void Unset();
}
