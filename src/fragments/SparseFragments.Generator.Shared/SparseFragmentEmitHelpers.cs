namespace SparseFragments.Generator.Shared;

/// <summary>Small shared primitives for fragment-stage emitters.</summary>
internal static class SparseFragmentEmitHelpers
{
    internal static string FragmentValueType(SparseMemberModel member) =>
        member.ChildModel is null ? member.Property.Type.Name : member.ChildFragmentType + "?";

    internal static string MergeStrategyField(string prefix, SparseMemberModel member) =>
        prefix + member.Id;

    internal static void AppendCloneContext(
        SharedIndentedBuilder code,
        int indent,
        string cloneContext,
        string referenceComparer
    ) =>
        code.AppendLineAt(
            indent,
            "var "
                + cloneContext
                + " = new global::System.Collections.Generic.Dictionary<object, object>("
                + referenceComparer
                + ".Instance);"
        );

    internal static void AppendNullGuard(
        SharedIndentedBuilder code,
        int indent,
        string variable
    )
    {
        code.AppendLineAt(indent, "if (" + variable + " is null)");
        code.AppendLineAt(indent, "{");
        code.AppendLineAt(
            indent + 1,
            "throw new global::System.ArgumentNullException(nameof(" + variable + "));"
        );
        code.AppendLineAt(indent, "}");
    }
}
