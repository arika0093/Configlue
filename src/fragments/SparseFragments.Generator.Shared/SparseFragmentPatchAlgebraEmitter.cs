using System.Collections.Immutable;
using System.Linq;

namespace SparseFragments.Generator.Shared;

/// <summary>Emits the standalone patch algebra (Between / Compose / Invert) for a generated patch.</summary>
internal static class SparseFragmentPatchAlgebraEmitter
{
    public static void AppendPatchAlgebra(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members
    )
    {
        var contract = SparseFragmentPatchEmitter.Contract(modelType, "Fragment");
        var prefix = SparseNaming.PatchApiPrefix(
            members.Select(static member => member.Property.Name)
        );
        var runtime = SparseFragmentPatchEmitter.Runtime;
        var optionalFragment = runtime + "Optional<Fragment?>";
        var wholeOperation = runtime + "FragmentOperation<Fragment?>";
        var kind = runtime + "FragmentOperationKind";
        var expressions = SparseFragmentPatchEmitter.Expressions;

        code.AppendLineAt(
            2,
            "/// <summary>Derives a patch between two sparse contribution states, preserving presence exactly.</summary>"
        );
        code.AppendLineAt(
            2,
            "public static Patch "
                + prefix
                + "Between("
                + optionalFragment
                + " before, "
                + optionalFragment
                + " after)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var patch = new Patch();");
        code.AppendLineAt(3, "if (before.IsPresent != after.IsPresent)");
        code.AppendLineAt(3, "{");
        code.AppendLineAt(
            4,
            "patch.__sparse_whole = after.IsPresent ? "
                + wholeOperation
                + ".Set(after.Value) : "
                + wholeOperation
                + ".Unset;"
        );
        code.AppendLineAt(4, "return patch;");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(3, "if (!before.IsPresent) return patch;");
        code.AppendLineAt(
            3,
            "if (global::System.Object.ReferenceEquals(before.Value, after.Value)) return patch;"
        );
        code.AppendLineAt(3, "if (before.Value is null || after.Value is null)");
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "patch.__sparse_whole = " + wholeOperation + ".Set(after.Value);");
        code.AppendLineAt(4, "return patch;");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(3, "var beforeFragment = before.Value!;");
        code.AppendLineAt(3, "var afterFragment = after.Value!;");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var field = SparseFragmentPatchEmitter.Field(member);
            if (member.ChildModel is null)
            {
                var operation =
                    runtime + "FragmentOperation<" + SparseFragmentPatchEmitter.ValueType(member) + ">";
                var beforeValue = "beforeFragment." + name + ".Value";
                var afterValue = "afterFragment." + name + ".Value";
                var equality = member.MergeStrategyType is null
                    ? expressions.ValueEqualityExpression(member, beforeValue, afterValue)
                    : "Fragment."
                        + SparseWellKnownNames.MergeStrategyFieldPrefix
                        + member.Id
                        + ".AreEqual("
                        + beforeValue
                        + ", "
                        + afterValue
                        + ")";
                code.AppendLineAt(3, "patch." + field + " = !afterFragment." + name + ".IsPresent");
                code.AppendLineAt(
                    4,
                    "? (beforeFragment."
                        + name
                        + ".IsPresent ? "
                        + operation
                        + ".Unset : default("
                        + operation
                        + "))"
                );
                code.AppendLineAt(
                    4,
                    ": ((beforeFragment."
                        + name
                        + ".IsPresent && "
                        + equality
                        + ") ? default("
                        + operation
                        + ") : "
                        + operation
                        + ".Set(afterFragment."
                        + name
                        + ".Value));"
                );
            }
            else
            {
                code.AppendLineAt(
                    3,
                    "patch."
                        + field
                        + " = "
                        + SparseFragmentPatchEmitter.ChildPatch(member)
                        + "."
                        + member.ChildModel.Value.PatchApiPrefix
                        + "Between(beforeFragment."
                        + name
                        + ", afterFragment."
                        + name
                        + ");"
                );
            }
        }

        code.AppendLineAt(3, "return patch;");
        code.AppendLineAt(2, "}");

        code.AppendLineAt(
            2,
            "/// <summary>Composes this patch with a following patch so both can be applied at once.</summary>"
        );
        code.AppendLineAt(2, "public Patch " + prefix + "Compose(Patch next)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if (next is null) throw new global::System.ArgumentNullException(nameof(next));"
        );
        code.AppendLineAt(3, "var result = new Patch();");
        code.AppendLineAt(3, "if (next.__sparse_whole.Kind != " + kind + ".Unchanged)");
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "result.__sparse_whole = next.__sparse_whole;");
        foreach (var member in members)
        {
            code.AppendLineAt(
                4,
                "result."
                    + SparseFragmentPatchEmitter.Field(member)
                    + " = next."
                    + SparseFragmentPatchEmitter.Field(member)
                    + ";"
            );
        }

        code.AppendLineAt(4, "return result;");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(3, "result.__sparse_whole = this.__sparse_whole;");
        foreach (var member in members)
        {
            var field = SparseFragmentPatchEmitter.Field(member);
            if (member.ChildModel is null)
            {
                code.AppendLineAt(
                    3,
                    "result."
                        + field
                        + " = next."
                        + field
                        + ".Kind == "
                        + kind
                        + ".Unchanged ? this."
                        + field
                        + " : next."
                        + field
                        + ";"
                );
            }
            else
            {
                code.AppendLineAt(
                    3,
                    "result."
                        + field
                        + " = next."
                        + field
                        + " is null ? this."
                        + field
                        + " : (this."
                        + field
                        + " is null ? next."
                        + field
                        + " : this."
                        + field
                        + "."
                        + member.ChildModel.Value.PatchApiPrefix
                        + "Compose(next."
                        + field
                        + "));"
                );
            }
        }

        code.AppendLineAt(3, "return result;");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "/// <summary>Composes two patches, applying <paramref name=\"second\"/> after <paramref name=\"first\"/>.</summary>"
        );
        code.AppendLineAt(
            2,
            "public static Patch " + prefix + "Compose(Patch first, Patch second)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if (first is null) throw new global::System.ArgumentNullException(nameof(first));"
        );
        code.AppendLineAt(3, "return first." + prefix + "Compose(second);");
        code.AppendLineAt(2, "}");

        code.AppendLineAt(
            2,
            "/// <summary>Inverts this patch relative to the sparse state it was applied to.</summary>"
        );
        code.AppendLineAt(
            2,
            "public Patch " + prefix + "Invert(" + optionalFragment + " baseline)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var applied = ((" + contract + ")this).Apply(baseline);");
        code.AppendLineAt(3, "return " + prefix + "Between(applied, baseline);");
        code.AppendLineAt(2, "}");
    }
}
