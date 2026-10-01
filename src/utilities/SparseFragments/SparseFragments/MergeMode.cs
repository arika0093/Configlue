namespace SparseFragments;

/// <summary>How a higher-priority fragment combines with a lower-priority value.</summary>
public enum MergeMode
{
    /// <summary>The higher-priority present value replaces the lower value.</summary>
    Replace,

    /// <summary>Nested model fragments are merged member by member.</summary>
    Deep,

    /// <summary>Present collections are concatenated from low to high priority.</summary>
    Append,

    /// <summary>Present collections are combined as an insertion-ordered set union.</summary>
    SetUnion,

    /// <summary>A generated member delegates all merge operations to a registered strategy type.</summary>
    Custom,
}
