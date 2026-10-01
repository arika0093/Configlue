namespace SparseFragments.Generator.Shared;

/// <summary>One validation policy for the fragment algebra's built-in merge modes.</summary>
internal static class SparseMergeValidation
{
    public static string? GetUnsupportedReason(
        int mode,
        bool hasChild,
        SparseCollectionKind collection
    ) =>
        mode switch
        {
            < 0 or > 4 => mode.ToString(),
            1 when !hasChild => "Deep",
            2 when collection == SparseCollectionKind.Set =>
                "Append on set types (use an ordered collection or SetUnion)",
            2 when collection == SparseCollectionKind.Unsupported => "Append",
            3 when collection == SparseCollectionKind.Unsupported => "SetUnion",
            _ => null,
        };
}
