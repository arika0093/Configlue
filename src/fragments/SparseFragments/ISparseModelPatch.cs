using System.ComponentModel;

namespace SparseFragments;

/// <summary>Typed whole-model and nested mutation operations for a generated patch.</summary>
/// <remarks>Advanced contract: implemented by generated patches.</remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public interface ISparseModelPatch<in TModel, TFragment>
    where TFragment : class
{
    /// <summary>Whether this patch leaves the contribution unchanged.</summary>
    bool IsEmpty { get; }

    /// <summary>Replaces the contribution with a model's fragment.</summary>
    void Set(TModel value);

    /// <summary>Sets the contribution to present null.</summary>
    void SetNull();

    /// <summary>Removes the contribution.</summary>
    void Unset();

    /// <summary>Applies whole-model and member operations to an optional contribution.</summary>
    Optional<TFragment?> Apply(Optional<TFragment?> current);
}
