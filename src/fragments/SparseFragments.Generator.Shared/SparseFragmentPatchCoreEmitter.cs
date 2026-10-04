using System;
using System.Collections.Immutable;
using System.Linq;

namespace SparseFragments.Generator.Shared;

/// <summary>Emits patch storage, whole-operation handling, construction and apply paths.</summary>
internal static class SparseFragmentPatchCoreEmitter
{
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
                var type =
                    runtime
                    + "FragmentOperation"
                    + "<"
                    + SparseFragmentPatchEmitter.ValueType(member)
                    + ">";
                code.AppendLineAt(2, "private " + type + " " + field + ";");
                code.AppendLineAt(2, "public ref " + type + " " + name + " => ref " + field + ";");
            }
            else
            {
                var type = SparseFragmentPatchEmitter.ChildPatch(member);
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

    /// <summary>Emits whole-operation field, empty helpers, Set/Unset, and implicit conversion.</summary>
    public static void AppendPatchWholeOperations(
        SharedIndentedBuilder code,
        string modelType,
        string contract,
        string emptyContract,
        ImmutableArray<SparseMemberModel> members,
        SparseFragmentPatchEmitter.SparsePatchDialect dialect
    )
    {
        var operation = SparseFragmentPatchEmitter.Operation(dialect);
        var kind = SparseFragmentPatchEmitter.Kind(dialect);
        code.AppendLineAt(
            2,
            "private " + operation + "<Fragment?> " + dialect.WholeFieldName + ";"
        );
        code.AppendLineAt(
            2,
            "private bool "
                + dialect.MembersEmptyName
                + " => "
                + SparseFragmentPatchEmitter.MembersEmptyExpression(members, dialect)
                + ";"
        );
        code.AppendLineAt(
            2,
            "bool "
                + emptyContract
                + ".IsEmpty => "
                + dialect.WholeFieldName
                + ".Kind == "
                + kind
                + ".Unchanged && "
                + dialect.MembersEmptyName
                + ";"
        );
        code.AppendLineAt(
            2,
            "void "
                + contract
                + ".Set("
                + modelType
                + " value) => "
                + dialect.WholeFieldName
                + " = "
                + operation
                + "<Fragment?>.Set(Fragment.From(value));"
        );
        code.AppendLineAt(
            2,
            "void "
                + contract
                + ".SetNull() => "
                + dialect.WholeFieldName
                + " = "
                + operation
                + "<Fragment?>.Set(null);"
        );
        code.AppendLineAt(
            2,
            "void "
                + contract
                + ".Unset() => "
                + dialect.WholeFieldName
                + " = "
                + operation
                + "<Fragment?>.Unset;"
        );
        foreach (var method in new[] { "Set", "SetNull", "Unset", "IsEmpty" })
        {
            if (members.Any(member => member.Property.Name == method))
                continue;
            var parameter = method == "Set" ? modelType + " value" : "";
            var argument = method == "Set" ? "value" : "";
            var declaration =
                method == "IsEmpty"
                    ? "public bool IsEmpty => ((" + emptyContract + ")this).IsEmpty;"
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

        code.AppendLineAt(
            2,
            "public static implicit operator Patch("
                + operation
                + "<Fragment?> operation) => new Patch { "
                + dialect.WholeFieldName
                + " = operation };"
        );
    }

    /// <summary>Emits Patch() and Patch(Fragment) construction from present members.</summary>
    public static void AppendPatchConstructor(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        SparseFragmentPatchEmitter.SparsePatchDialect dialect
    )
    {
        var operation = SparseFragmentPatchEmitter.Operation(dialect);
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
            var field = dialect.MemberField(member);
            if (member.ChildModel is null)
                code.AppendLineAt(
                    3,
                    field
                        + " = fragment."
                        + name
                        + ".IsPresent ? "
                        + operation
                        + "<"
                        + SparseFragmentPatchEmitter.ValueType(member)
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
                        + SparseFragmentPatchEmitter.ChildPatch(member)
                        + "() : new "
                        + SparseFragmentPatchEmitter.ChildPatch(member)
                        + "(fragment."
                        + name
                        + ".Value);"
                );
                code.AppendLineAt(
                    4,
                    "if (fragment."
                        + name
                        + ".Value is null) (("
                        + dialect.NestedContract(member)
                        + ")"
                        + field
                        + ").SetNull();"
                );
                code.AppendLineAt(3, "}");
            }
        }
        code.AppendLineAt(2, "}");
    }

    /// <summary>Emits per-member ApplyMembers used by both Optional apply paths.</summary>
    public static void AppendPatchApplyMembers(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        SparseFragmentPatchEmitter.SparsePatchDialect dialect
    )
    {
        code.AppendLineAt(2, "internal Fragment ApplyMembers(Fragment current) => new Fragment");
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var field = dialect.MemberField(member);
            string expression;
            if (member.ChildModel is null)
            {
                expression = field + ".Apply(current." + name + ")";
            }
            else if (dialect.CastNestedApply)
            {
                expression =
                    field
                    + " is null ? current."
                    + name
                    + " : (("
                    + dialect.NestedContract(member)
                    + ")"
                    + field
                    + ")."
                    + dialect.NestedApplyMethod
                    + "(current."
                    + name
                    + ")";
            }
            else
            {
                expression =
                    field
                    + " is null ? current."
                    + name
                    + " : "
                    + field
                    + "."
                    + dialect.NestedApplyMethod
                    + "(current."
                    + name
                    + ")";
            }
            code.AppendLineAt(3, name + " = " + expression + ",");
        }
        code.AppendLineAt(2, "};");
    }

    /// <summary>Emits the Optional apply path; standalone uses explicit contract, Configlue uses internal ApplyNested.</summary>
    public static void AppendPatchOptionalApply(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        SparseFragmentPatchEmitter.SparsePatchDialect dialect,
        string methodDeclaration
    )
    {
        var optional = SparseFragmentPatchEmitter.OptionalFragment(dialect);
        code.AppendLineAt(2, methodDeclaration);
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "current = " + dialect.WholeFieldName + ".Apply(current);");
        code.AppendLineAt(3, "if (" + dialect.MembersEmptyName + ") return current;");
        code.AppendLineAt(
            3,
            "var basis = current.IsPresent && current.Value is not null ? current.Value : new Fragment();"
        );
        code.AppendLineAt(3, "return " + optional + ".Present(ApplyMembers(basis));");
        code.AppendLineAt(2, "}");
    }
}
