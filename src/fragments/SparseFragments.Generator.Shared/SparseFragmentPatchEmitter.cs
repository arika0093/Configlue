using System;
using System.Collections.Immutable;
using System.Linq;

namespace SparseFragments.Generator.Shared;

/// <summary>Emits typed standalone mutations without source routing or ownership.</summary>
internal static class SparseFragmentPatchEmitter
{
    private const string Runtime = "global::SparseFragments.";

    private static string Field(SparseMemberModel member) => "__sparse_patch_member_" + member.Id;

    private static string ValueType(SparseMemberModel member) =>
        member.ChildModel is null ? member.Property.Type.Name : member.ChildFragmentType + "?";

    private static string ChildPatch(SparseMemberModel member) =>
        member.ChildFragmentType!.Substring(0, member.ChildFragmentType.Length - "Fragment".Length)
        + "Patch";

    private static string Contract(string modelType, string fragmentType) =>
        Runtime + "ISparseModelPatch<" + modelType + ", " + fragmentType + ">";

    private static string ChildContract(SparseMemberModel member) =>
        Contract(member.ChildModel!.Value.NonNullableName, member.ChildFragmentType!);

    public static void AppendFragmentMethods(SharedIndentedBuilder code, string modelType)
    {
        code.AppendLineAt(2, "public Patch ToPatch() => new(this);");
        code.AppendLineAt(2, "public Fragment Apply(Patch patch)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if (patch is null) throw new global::System.ArgumentNullException(nameof(patch));"
        );
        code.AppendLineAt(
            3,
            "var result = (("
                + Contract(modelType, "Fragment")
                + ")patch).Apply("
                + Runtime
                + "Optional<Fragment?>.Present(this));"
        );
        code.AppendLineAt(
            3,
            "if (!result.IsPresent || result.Value is null) throw new global::System.InvalidOperationException(\"Apply a whole-contribution null or unset operation through ISparseModelPatch.Apply to preserve its optional state.\");"
        );
        code.AppendLineAt(3, "return result.Value;");
        code.AppendLineAt(2, "}");
    }

    public static void AppendPatchMembers(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        string runtime,
        Func<SparseMemberModel, string> fieldName
    )
    {
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var field = fieldName(member);
            if (member.ChildModel is null)
            {
                var type = runtime + "FragmentOperation" + "<" + ValueType(member) + ">";
                code.AppendLineAt(2, "private " + type + " " + field + ";");
                code.AppendLineAt(2, "public ref " + type + " " + name + " => ref " + field + ";");
            }
            else
            {
                var type = ChildPatch(member);
                code.AppendLineAt(2, "private " + type + "? " + field + ";");
                code.AppendLineAt(
                    2,
                    "public "
                        + type
                        + " "
                        + name
                        + " { get => "
                        + field
                        + " ??= new "
                        + type
                        + "(); set => "
                        + field
                        + " = value; }"
                );
            }
        }
    }

    public static void AppendPatch(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members
    )
    {
        var contract = Contract(modelType, "Fragment");
        var operation = Runtime + "FragmentOperation";
        var kind = Runtime + "FragmentOperationKind";
        var optional = Runtime + "Optional<Fragment?>";
        code.AppendLineAt(1, "public sealed class Patch : " + contract);
        code.AppendLineAt(1, "{");
        AppendPatchMembers(code, members, Runtime, Field);

        var memberEmpty = members.IsEmpty
            ? "true"
            : string.Join(
                " && ",
                members.Select(member =>
                    member.ChildModel is null
                        ? Field(member) + ".Kind == " + kind + ".Unchanged"
                        : "("
                            + Field(member)
                            + " is null || (("
                            + ChildContract(member)
                            + ")"
                            + Field(member)
                            + ").IsEmpty)"
                )
            );
        code.AppendLineAt(2, "private " + operation + "<Fragment?> __sparse_whole;");
        code.AppendLineAt(2, "private bool __SparseMembersEmpty => " + memberEmpty + ";");
        code.AppendLineAt(
            2,
            "bool "
                + contract
                + ".IsEmpty => __sparse_whole.Kind == "
                + kind
                + ".Unchanged && __SparseMembersEmpty;"
        );
        code.AppendLineAt(
            2,
            "void "
                + contract
                + ".Set("
                + modelType
                + " value) => __sparse_whole = "
                + operation
                + "<Fragment?>.Set(Fragment.From(value));"
        );
        code.AppendLineAt(
            2,
            "void "
                + contract
                + ".SetNull() => __sparse_whole = "
                + operation
                + "<Fragment?>.Set(null);"
        );
        code.AppendLineAt(
            2,
            "void " + contract + ".Unset() => __sparse_whole = " + operation + "<Fragment?>.Unset;"
        );
        foreach (var method in new[] { "Set", "SetNull", "Unset", "IsEmpty" })
        {
            if (members.Any(member => member.Property.Name == method))
                continue;
            var parameter = method == "Set" ? modelType + " value" : "";
            var argument = method == "Set" ? "value" : "";
            var declaration =
                method == "IsEmpty"
                    ? "public bool IsEmpty => ((" + contract + ")this).IsEmpty;"
                    : "public void "
                        + method
                        + "("
                        + parameter
                        + ") => (("
                        + contract
                        + ")this)."
                        + method
                        + "("
                        + argument
                        + ");";
            code.AppendLineAt(2, declaration);
        }

        code.AppendLineAt(2, "public Patch() { }");
        code.AppendLineAt(2, "public Patch(Fragment fragment)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if (fragment is null) throw new global::System.ArgumentNullException(nameof(fragment));"
        );
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var field = Field(member);
            if (member.ChildModel is null)
                code.AppendLineAt(
                    3,
                    field
                        + " = fragment."
                        + name
                        + ".IsPresent ? "
                        + operation
                        + "<"
                        + ValueType(member)
                        + ">.Set(fragment."
                        + name
                        + ".Value) : default;"
                );
            else
            {
                code.AppendLineAt(3, "if (fragment." + name + ".IsPresent)");
                code.AppendLineAt(3, "{");
                code.AppendLineAt(
                    4,
                    field
                        + " = fragment."
                        + name
                        + ".Value is null ? new "
                        + ChildPatch(member)
                        + "() : new "
                        + ChildPatch(member)
                        + "(fragment."
                        + name
                        + ".Value);"
                );
                code.AppendLineAt(
                    4,
                    "if (fragment."
                        + name
                        + ".Value is null) (("
                        + ChildContract(member)
                        + ")"
                        + field
                        + ").SetNull();"
                );
                code.AppendLineAt(3, "}");
            }
        }
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, optional + " " + contract + ".Apply(" + optional + " current)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "current = __sparse_whole.Apply(current);");
        code.AppendLineAt(3, "if (__SparseMembersEmpty) return current;");
        code.AppendLineAt(
            3,
            "var basis = current.IsPresent && current.Value is not null ? current.Value : new Fragment();"
        );
        code.AppendLineAt(3, "return " + optional + ".Present(ApplyMembers(basis));");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "internal Fragment ApplyMembers(Fragment current) => new Fragment");
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var field = Field(member);
            code.AppendLineAt(
                3,
                name
                    + " = "
                    + (
                        member.ChildModel is null
                            ? field + ".Apply(current." + name + ")"
                            : field
                                + " is null ? current."
                                + name
                                + " : (("
                                + ChildContract(member)
                                + ")"
                                + field
                                + ").Apply(current."
                                + name
                                + ")"
                    )
                    + ","
            );
        }
        code.AppendLineAt(2, "};");
        code.AppendLineAt(
            2,
            "public static implicit operator Patch("
                + operation
                + "<Fragment?> operation) => new Patch { __sparse_whole = operation };"
        );
        code.AppendLineAt(1, "}");
    }
}
