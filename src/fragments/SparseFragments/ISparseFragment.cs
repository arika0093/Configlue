using System.ComponentModel;

namespace SparseFragments;

/// <summary>Non-generic access used by codecs and diagnostics on cold paths.</summary>
/// <remarks>Advanced tooling surface: application code uses the generated typed fragments.</remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public interface ISparseFragment
{
    /// <summary>Enumerates only members present in this sparse contribution.</summary>
    IEnumerable<SparseFragmentMember> EnumeratePresentMembers();

    /// <summary>Returns a copy with the specified member set to a present value.</summary>
    ISparseFragment WithMember(int memberId, object? value);

    /// <summary>Returns a copy with the specified member absent from this sparse contribution.</summary>
    ISparseFragment WithoutMember(int memberId);
}
