using System;
using System.Collections.Immutable;

namespace Configlue.Generator;

// Ported from SparseFragments.Generator.Shared.SparseJsonPatchEmitter
// (AppendConfiglueJsonBetween), which upstream removed when dropping
// Configlue-era remnants. Configlue patches have no public Between; this
// internal helper derives the same semantic patch member-wise for the JSON
// Patch import bridge. Whole-contribution transitions use the whole
// operation, nested members recurse, and scalar members use ordinal default
// equality (over-setting is semantically harmless, under-setting never
// happens).
internal static class ConfiglueJsonBetweenEmitter
{
    public static void AppendConfiglueJsonBetween(
        SparseFragments.Generator.Shared.SharedIndentedBuilder code,
        ImmutableArray<SparseFragments.Generator.Shared.SparseMemberModel> members,
        Func<SparseFragments.Generator.Shared.SparseMemberModel, string> backingField,
        Func<SparseFragments.Generator.Shared.SparseMemberModel, string> valueType,
        Func<SparseFragments.Generator.Shared.SparseMemberModel, string> nestedPatchBetween
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            2,
            "/// <summary>Derives a semantic patch between two sparse contribution states.</summary>"
        );
        code.AppendLineAt(
            2,
            "internal static Patch __ConfiglueJsonBetween(global::Configlue.Optional<Fragment?> before, global::Configlue.Optional<Fragment?> after)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var patch = new Patch();");
        code.AppendLineAt(3, "if (before.IsPresent != after.IsPresent)");
        code.AppendLineAt(3, "{");
        code.AppendLineAt(
            4,
            "patch.__configlue_whole_operation = after.IsPresent ? global::Configlue.FragmentOperation<Fragment?>.Set(after.Value) : global::Configlue.FragmentOperation<Fragment?>.Unset;"
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
        code.AppendLineAt(
            4,
            "patch.__configlue_whole_operation = global::Configlue.FragmentOperation<Fragment?>.Set(after.Value);"
        );
        code.AppendLineAt(4, "return patch;");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(3, "var beforeFragment = before.Value!;");
        code.AppendLineAt(3, "var afterFragment = after.Value!;");
        foreach (var member in members)
        {
            var name = SparseFragments.Generator.Shared.SparseNaming.EscapeIdentifier(
                member.Property.Name
            );
            if (member.ChildModel is null)
            {
                var operation = "global::Configlue.FragmentOperation<" + valueType(member) + ">";
                code.AppendLineAt(3, "if (!afterFragment." + name + ".IsPresent)");
                code.AppendLineAt(
                    4,
                    "patch."
                        + name
                        + " = beforeFragment."
                        + name
                        + ".IsPresent ? "
                        + operation
                        + ".Unset : default;"
                );
                code.AppendLineAt(3, "else if (!beforeFragment." + name + ".IsPresent)");
                code.AppendLineAt(
                    4,
                    "patch." + name + " = " + operation + ".Set(afterFragment." + name + ".Value);"
                );
                code.AppendLineAt(
                    3,
                    "else if (!global::System.Collections.Generic.EqualityComparer<"
                        + valueType(member)
                        + ">.Default.Equals(beforeFragment."
                        + name
                        + ".Value!, afterFragment."
                        + name
                        + ".Value!))"
                );
                code.AppendLineAt(
                    4,
                    "patch." + name + " = " + operation + ".Set(afterFragment." + name + ".Value);"
                );
            }
            else
            {
                code.AppendLineAt(
                    3,
                    "patch."
                        + backingField(member)
                        + " = "
                        + nestedPatchBetween(member)
                        + "(beforeFragment."
                        + name
                        + ", afterFragment."
                        + name
                        + ");"
                );
            }
        }

        code.AppendLineAt(3, "return patch;");
        code.AppendLineAt(2, "}");
    }
}
