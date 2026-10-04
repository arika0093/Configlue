using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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

    /// <summary>Small dialect for shared patch-core emission (whole, empty, ctor, apply).</summary>
    /// <remarks>
    /// Only genuinely-shared algebra lives here: runtime names plus field/contract hooks.
    /// Routing, replacement, and facade contracts stay in the product generators.
    /// </remarks>
    internal readonly record struct SparsePatchDialect(
        string RuntimeNamespace,
        string WholeFieldName,
        string MembersEmptyName,
        Func<SparseMemberModel, string> MemberField,
        Func<SparseMemberModel, string> NestedContract,
        string NestedApplyMethod,
        bool CastNestedApply
    );

    internal static SparsePatchDialect StandaloneDialect() =>
        new(Runtime, "__sparse_whole", "__SparseMembersEmpty", Field, ChildContract, "Apply", true);

    private static string Operation(SparsePatchDialect dialect) =>
        dialect.RuntimeNamespace + "FragmentOperation";

    private static string Kind(SparsePatchDialect dialect) =>
        dialect.RuntimeNamespace + "FragmentOperationKind";

    private static string OptionalFragment(SparsePatchDialect dialect) =>
        dialect.RuntimeNamespace + "Optional<Fragment?>";

    private static string MembersEmptyExpression(
        ImmutableArray<SparseMemberModel> members,
        SparsePatchDialect dialect
    )
    {
        if (members.IsEmpty)
            return "true";
        return string.Join(
            " && ",
            members.Select(member =>
                member.ChildModel is null
                    ? dialect.MemberField(member) + ".Kind == " + Kind(dialect) + ".Unchanged"
                    : "("
                        + dialect.MemberField(member)
                        + " is null || (("
                        + dialect.NestedContract(member)
                        + ")"
                        + dialect.MemberField(member)
                        + ").IsEmpty)"
            )
        );
    }

    /// <summary>Emits whole-operation field, empty helpers, Set/Unset, and implicit conversion.</summary>
    public static void AppendPatchWholeOperations(
        SharedIndentedBuilder code,
        string modelType,
        string contract,
        string emptyContract,
        ImmutableArray<SparseMemberModel> members,
        SparsePatchDialect dialect
    )
    {
        var operation = Operation(dialect);
        var kind = Kind(dialect);
        code.AppendLineAt(
            2,
            "private " + operation + "<Fragment?> " + dialect.WholeFieldName + ";"
        );
        code.AppendLineAt(
            2,
            "private bool "
                + dialect.MembersEmptyName
                + " => "
                + MembersEmptyExpression(members, dialect)
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
        SparsePatchDialect dialect
    )
    {
        var operation = Operation(dialect);
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
        SparsePatchDialect dialect
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
        SparsePatchDialect dialect,
        string methodDeclaration
    )
    {
        var optional = OptionalFragment(dialect);
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

    public static void AppendPatch(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool hasJsonPatch = false,
        string jsonPrefix = ""
    )
    {
        var contract = Contract(modelType, "Fragment");
        var optional = Runtime + "Optional<Fragment?>";
        var patchPrefix = SparseNaming.PatchApiPrefix(
            members.Select(static member => member.Property.Name)
        );
        code.AppendLineAt(1, "public sealed class Patch : " + contract);
        code.AppendLineAt(1, "{");
        AppendPatchMembers(code, members, Runtime, Field);
        var dialect = StandaloneDialect();
        AppendPatchWholeOperations(code, modelType, contract, contract, members, dialect);
        AppendPatchConstructor(code, members, dialect);
        AppendPatchOptionalApply(
            code,
            members,
            dialect,
            optional + " " + contract + ".Apply(" + optional + " current)"
        );
        AppendPatchApplyMembers(code, members, dialect);
        AppendPatchAlgebra(code, modelType, members);
        AppendPatchRebase(code, modelType, members);
        if (hasJsonPatch)
        {
            SparseJsonPatchEmitter.AppendFragmentJsonHelpers(
                code,
                "global::SparseFragments",
                Runtime + "Optional"
            );
            SparseJsonPatchEmitter.AppendFromJsonPatch(
                code,
                "global::SparseFragments",
                Runtime + "Optional",
                jsonPrefix,
                patchPrefix + "Between"
            );
            SparseJsonPatchEmitter.AppendToJsonPatch(
                code,
                "global::SparseFragments",
                Runtime + "Optional",
                jsonPrefix,
                "((" + contract + ")this).Apply(baseline)"
            );
        }
        code.AppendLineAt(1, "}");
    }

    private static readonly SparseFragmentExpressions Expressions = new("__sparse_patch_context");

    /// <summary>Emits the standalone patch algebra (Between / Compose / Invert) for a generated patch.</summary>
    public static void AppendPatchAlgebra(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members
    )
    {
        var contract = Contract(modelType, "Fragment");
        var prefix = SparseNaming.PatchApiPrefix(
            members.Select(static member => member.Property.Name)
        );
        var optionalFragment = Runtime + "Optional<Fragment?>";
        var wholeOperation = Runtime + "FragmentOperation<Fragment?>";
        var kind = Runtime + "FragmentOperationKind";

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
            var field = Field(member);
            if (member.ChildModel is null)
            {
                var operation = Runtime + "FragmentOperation<" + ValueType(member) + ">";
                var beforeValue = "beforeFragment." + name + ".Value";
                var afterValue = "afterFragment." + name + ".Value";
                var equality = member.MergeStrategyType is null
                    ? Expressions.ValueEqualityExpression(member, beforeValue, afterValue)
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
                        + ChildPatch(member)
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
            code.AppendLineAt(4, "result." + Field(member) + " = next." + Field(member) + ";");
        }

        code.AppendLineAt(4, "return result;");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(3, "result.__sparse_whole = this.__sparse_whole;");
        foreach (var member in members)
        {
            var field = Field(member);
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

    private static string EqualMethod(SparseMemberModel member) => "__SparseEqual_" + member.Id;

    /// <summary>Emits the standalone three-way rebase with structured conflicts.</summary>
    public static void AppendPatchRebase(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members
    )
    {
        var contract = Contract(modelType, "Fragment");
        var prefix = SparseNaming.PatchApiPrefix(
            members.Select(static member => member.Property.Name)
        );
        var optionalFragment = Runtime + "Optional<Fragment?>";
        var operation = Runtime + "FragmentOperation";
        var kind = Runtime + "FragmentOperationKind";
        var comparer = Runtime + "SparseFragmentComparer";
        var conflict = "global::SparseFragments.SparsePatchConflict";
        var conflictKind = "global::SparseFragments.SparsePatchConflictKind";
        var conflictList =
            "global::System.Collections.Generic.List<global::SparseFragments.SparsePatchConflict>";
        var rebaseResult = "global::SparseFragments.RebaseResult<Patch>";

        foreach (var member in members)
        {
            var optionalMember = Runtime + "Optional<" + ValueType(member) + ">";
            var method = EqualMethod(member);
            if (member.ChildModel is not null)
            {
                code.AppendIndent(2)
                    .Append("private static bool ")
                    .Append(method)
                    .Append("(")
                    .Append(optionalMember)
                    .Append(" left, ")
                    .Append(optionalMember)
                    .Append(" right) => ")
                    .Append(comparer)
                    .AppendLine(".AreEqual(left, right);");
                continue;
            }

            var equality = member.MergeStrategyType is null
                ? Expressions.ValueEqualityExpression(member, "left.Value", "right.Value")
                : "Fragment."
                    + SparseWellKnownNames.MergeStrategyFieldPrefix
                    + member.Id
                    + ".AreEqual(left.Value, right.Value)";
            code.AppendIndent(2)
                .Append("private static bool ")
                .Append(method)
                .Append("(")
                .Append(optionalMember)
                .Append(" left, ")
                .Append(optionalMember)
                .AppendLine(" right)");
            code.AppendLineAt(2, "{");
            code.AppendLineAt(3, "if (!left.IsPresent) return !right.IsPresent;");
            code.AppendLineAt(3, "return right.IsPresent && " + equality + ";");
            code.AppendLineAt(2, "}");
        }

        code.AppendIndent(2)
            .Append(
                "private static "
                    + Runtime
                    + "Optional<object?> __SparseState("
                    + optionalFragment
                    + " state) => state.IsPresent ? "
                    + Runtime
                    + "Optional<object?>.Present((object?)state.Value) : "
                    + Runtime
                    + "Optional<object?>.Missing;"
            )
            .AppendLine();
        code.AppendIndent(2)
            .Append(
                "private static "
                    + Runtime
                    + "Optional<object?> __SparseMember<T>("
                    + Runtime
                    + "Optional<T> value) => value.IsPresent ? "
                    + Runtime
                    + "Optional<object?>.Present((object?)value.Value) : "
                    + Runtime
                    + "Optional<object?>.Missing;"
            )
            .AppendLine();

        code.AppendLineAt(
            2,
            "/// <summary>Rebases a local patch onto a newer sparse state and reports structured conflicts.</summary>"
        );
        code.AppendLineAt(
            2,
            "public static "
                + rebaseResult
                + " "
                + prefix
                + "Rebase("
                + optionalFragment
                + " baseState, Patch local, "
                + optionalFragment
                + " currentState)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if (local is null) throw new global::System.ArgumentNullException(nameof(local));"
        );
        code.AppendLineAt(
            3,
            "if ((("
                + contract
                + ")local).IsEmpty) return "
                + rebaseResult
                + ".Success(new Patch());"
        );
        code.AppendLineAt(3, "var result = new Patch();");
        code.AppendLineAt(3, "var conflicts = new " + conflictList + "();");
        code.AppendLineAt(3, "var desiredState = ((" + contract + ")local).Apply(baseState);");
        code.AppendLineAt(3, "if (local.__sparse_whole.Kind != " + kind + ".Unchanged)");
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "if (" + comparer + ".AreEqual(baseState, currentState))");
        code.AppendLineAt(4, "{");
        code.AppendLineAt(4, "    result = local." + prefix + "Compose(new Patch());");
        code.AppendLineAt(4, "}");
        code.AppendLineAt(4, "else if (!" + comparer + ".AreEqual(desiredState, currentState))");
        code.AppendLineAt(4, "{");
        code.AppendLineAt(
            4,
            "    conflicts.Add(new "
                + conflict
                + "(new string[0], "
                + conflictKind
                + ".WholeContribution, __SparseState(baseState), __SparseState(desiredState), __SparseState(currentState), \"The whole contribution conflicts with a concurrent change.\"));"
        );
        code.AppendLineAt(4, "}");
        code.AppendLineAt(4, "return new " + rebaseResult + "(result, conflicts);");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(
            3,
            "if (!baseState.IsPresent || !currentState.IsPresent || baseState.Value is null || currentState.Value is null || !desiredState.IsPresent || desiredState.Value is null)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "if (" + comparer + ".AreEqual(baseState, currentState))");
        code.AppendLineAt(4, "{");
        code.AppendLineAt(4, "    result = local." + prefix + "Compose(new Patch());");
        code.AppendLineAt(4, "}");
        code.AppendLineAt(4, "else if (!" + comparer + ".AreEqual(desiredState, currentState))");
        code.AppendLineAt(4, "{");
        code.AppendLineAt(
            4,
            "    conflicts.Add(new "
                + conflict
                + "(new string[0], "
                + conflictKind
                + ".WholeContribution, __SparseState(baseState), __SparseState(desiredState), __SparseState(currentState), \"The contribution conflicts with a concurrent change.\"));"
        );
        code.AppendLineAt(4, "}");
        code.AppendLineAt(4, "return new " + rebaseResult + "(result, conflicts);");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(3, "var baseFragment = baseState.Value!;");
        code.AppendLineAt(3, "var currentFragment = currentState.Value!;");

        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var field = Field(member);
            var baseMember = "baseMember";
            var currentMember = "currentMember";
            var desiredMember = "desiredMember";
            var equality = EqualMethod(member);

            code.AppendLineAt(3, "{");
            if (member.ChildModel is not null)
            {
                code.AppendLineAt(
                    4,
                    "if (local."
                        + field
                        + " is not null && !(("
                        + ChildContract(member)
                        + ")local."
                        + field
                        + ").IsEmpty)"
                );
                code.AppendLineAt(4, "{");
                code.AppendLineAt(5, "var " + baseMember + " = baseFragment." + name + ";");
                code.AppendLineAt(5, "var " + currentMember + " = currentFragment." + name + ";");
                code.AppendLineAt(
                    5,
                    "var "
                        + desiredMember
                        + " = (("
                        + ChildContract(member)
                        + ")local."
                        + field
                        + ").Apply("
                        + baseMember
                        + ");"
                );
                code.AppendLineAt(
                    5,
                    "if (" + comparer + ".AreEqual(" + baseMember + ", " + currentMember + "))"
                );
                code.AppendLineAt(5, "{");
                code.AppendLineAt(6, "result." + field + " = local." + field + ";");
                code.AppendLineAt(5, "}");
                code.AppendLineAt(
                    5,
                    "else if (" + baseMember + ".IsPresent && " + currentMember + ".IsPresent)"
                );
                code.AppendLineAt(5, "{");
                code.AppendLineAt(
                    6,
                    "var nested = "
                        + ChildPatch(member)
                        + "."
                        + member.ChildModel.Value.PatchApiPrefix
                        + "Rebase("
                        + baseMember
                        + ", local."
                        + field
                        + ", "
                        + currentMember
                        + ");"
                );
                code.AppendLineAt(6, "result." + field + " = nested.Patch;");
                code.AppendLineAt(6, "foreach (var nestedConflict in nested.Conflicts)");
                code.AppendLineAt(6, "{");
                code.AppendLineAt(
                    7,
                    "conflicts.Add(nestedConflict.WithPathPrefix("
                        + SymbolDisplay.FormatLiteral(member.Property.Name, true)
                        + "));"
                );
                code.AppendLineAt(6, "}");
                code.AppendLineAt(5, "}");
                code.AppendLineAt(
                    5,
                    "else if (!"
                        + comparer
                        + ".AreEqual("
                        + desiredMember
                        + ", "
                        + currentMember
                        + "))"
                );
                code.AppendLineAt(5, "{");
                code.AppendLineAt(
                    6,
                    "conflicts.Add(new "
                        + conflict
                        + "(new string[] { "
                        + SymbolDisplay.FormatLiteral(member.Property.Name, true)
                        + " }, "
                        + conflictKind
                        + ".Nested, __SparseMember("
                        + baseMember
                        + "), __SparseMember("
                        + desiredMember
                        + "), __SparseMember("
                        + currentMember
                        + "), \"The nested contribution conflicts with a concurrent change.\"));"
                );
                code.AppendLineAt(5, "}");
                code.AppendLineAt(4, "}");
            }
            else
            {
                var operationType = operation + "<" + ValueType(member) + ">";
                string scalarKind;
                if (member.MergeMode == 2)
                {
                    scalarKind = conflictKind + ".CollectionAppend";
                }
                else if (member.MergeMode == 3)
                {
                    scalarKind = conflictKind + ".CollectionSetUnion";
                }
                else
                {
                    scalarKind = conflictKind + ".Scalar";
                }
                if (member.MergeStrategyType is not null)
                {
                    code.AppendLineAt(4, "if (local." + field + ".Kind == " + kind + ".Set)");
                    code.AppendLineAt(4, "{");
                    code.AppendLineAt(5, "var " + baseMember + " = baseFragment." + name + ";");
                    code.AppendLineAt(
                        5,
                        "var " + currentMember + " = currentFragment." + name + ";"
                    );
                    code.AppendLineAt(
                        5,
                        "var " + desiredMember + " = local." + field + ".Apply(" + baseMember + ");"
                    );
                    var strategyField =
                        "Fragment." + SparseWellKnownNames.MergeStrategyFieldPrefix + member.Id;
                    code.AppendLineAt(
                        5,
                        "if ("
                            + strategyField
                            + ".TryRebase("
                            + baseMember
                            + ".IsPresent ? "
                            + baseMember
                            + ".Value : default, "
                            + desiredMember
                            + ".IsPresent ? "
                            + desiredMember
                            + ".Value : default, "
                            + currentMember
                            + ".IsPresent ? "
                            + currentMember
                            + ".Value : default, out var rebasedValue, out var reason))"
                    );
                    code.AppendLineAt(5, "{");
                    code.AppendLineAt(
                        6,
                        "result." + field + " = " + operationType + ".Set(rebasedValue);"
                    );
                    code.AppendLineAt(5, "}");
                    code.AppendLineAt(5, "else");
                    code.AppendLineAt(5, "{");
                    code.AppendLineAt(
                        6,
                        "conflicts.Add(new "
                            + conflict
                            + "(new string[] { "
                            + SymbolDisplay.FormatLiteral(member.Property.Name, true)
                            + " }, "
                            + conflictKind
                            + ".CustomStrategy, __SparseMember("
                            + baseMember
                            + "), __SparseMember("
                            + desiredMember
                            + "), __SparseMember("
                            + currentMember
                            + "), reason ?? \"The custom merge strategy could not rebase the member.\"));"
                    );
                    code.AppendLineAt(5, "}");
                    code.AppendLineAt(4, "}");
                    code.AppendLineAt(
                        4,
                        "else if (local." + field + ".Kind != " + kind + ".Unchanged)"
                    );
                    code.AppendLineAt(4, "{");
                    code.AppendLineAt(5, "var " + baseMember + " = baseFragment." + name + ";");
                    code.AppendLineAt(
                        5,
                        "var " + currentMember + " = currentFragment." + name + ";"
                    );
                    code.AppendLineAt(
                        5,
                        "var " + desiredMember + " = local." + field + ".Apply(" + baseMember + ");"
                    );
                    EmitScalarRebase(
                        code,
                        field,
                        member.Property.Name,
                        baseMember,
                        desiredMember,
                        currentMember,
                        equality,
                        scalarKind,
                        conflict,
                        5
                    );
                    code.AppendLineAt(4, "}");
                }
                else if (member.MergeMode is 2 or 3)
                {
                    code.AppendLineAt(4, "if (local." + field + ".Kind != " + kind + ".Unchanged)");
                    code.AppendLineAt(4, "{");
                    code.AppendLineAt(5, "var " + baseMember + " = baseFragment." + name + ";");
                    code.AppendLineAt(
                        5,
                        "var " + currentMember + " = currentFragment." + name + ";"
                    );
                    code.AppendLineAt(
                        5,
                        "var " + desiredMember + " = local." + field + ".Apply(" + baseMember + ");"
                    );
                    code.AppendLineAt(5, "var handled = false;");
                    code.AppendLineAt(
                        5,
                        "if ("
                            + baseMember
                            + ".IsPresent && "
                            + baseMember
                            + ".Value is not null && "
                            + currentMember
                            + ".IsPresent && "
                            + currentMember
                            + ".Value is not null && "
                            + desiredMember
                            + ".IsPresent && "
                            + desiredMember
                            + ".Value is not null)"
                    );
                    code.AppendLineAt(5, "{");
                    code.AppendLineAt(6, "handled = true;");
                    code.AppendLineAt(
                        6,
                        "var beforeValues = global::System.Linq.Enumerable.ToList(global::System.Linq.Enumerable.Cast<object?>((global::System.Collections.IEnumerable)"
                            + baseMember
                            + ".Value));"
                    );
                    code.AppendLineAt(
                        6,
                        "var currentValues = global::System.Linq.Enumerable.ToList(global::System.Linq.Enumerable.Cast<object?>((global::System.Collections.IEnumerable)"
                            + currentMember
                            + ".Value));"
                    );
                    code.AppendLineAt(
                        6,
                        "var desiredValues = global::System.Linq.Enumerable.ToList(global::System.Linq.Enumerable.Cast<object?>((global::System.Collections.IEnumerable)"
                            + desiredMember
                            + ".Value));"
                    );
                    code.AppendLineAt(
                        6,
                        "if ("
                            + Runtime
                            + "SparseCollectionRebase."
                            + (member.MergeMode == 2 ? "TryRebaseAppend" : "TryRebaseSetUnion")
                            + "(beforeValues, desiredValues, currentValues, (object? left, object? right) => "
                            + Runtime
                            + "SparseValueComparer.AreEqual(left, right), out var rebasedValues, out var reason))"
                    );
                    code.AppendLineAt(6, "{");
                    var materialized = SparseFragmentExpressions.MaterializeCollection(
                        member,
                        "global::System.Linq.Enumerable.Cast<"
                            + member.Collection.ElementType.Name
                            + ">(rebasedValues)"
                    );
                    code.AppendLineAt(
                        7,
                        "result." + field + " = " + operationType + ".Set(" + materialized + ");"
                    );
                    code.AppendLineAt(6, "}");
                    code.AppendLineAt(6, "else");
                    code.AppendLineAt(6, "{");
                    code.AppendLineAt(
                        7,
                        "conflicts.Add(new "
                            + conflict
                            + "(new string[] { "
                            + SymbolDisplay.FormatLiteral(member.Property.Name, true)
                            + " }, "
                            + scalarKind
                            + ", __SparseMember("
                            + baseMember
                            + "), __SparseMember("
                            + desiredMember
                            + "), __SparseMember("
                            + currentMember
                            + "), reason));"
                    );
                    code.AppendLineAt(6, "}");
                    code.AppendLineAt(5, "}");
                    code.AppendLineAt(5, "if (!handled)");
                    code.AppendLineAt(5, "{");
                    EmitScalarRebase(
                        code,
                        field,
                        member.Property.Name,
                        baseMember,
                        desiredMember,
                        currentMember,
                        equality,
                        scalarKind,
                        conflict,
                        6
                    );
                    code.AppendLineAt(5, "}");
                    code.AppendLineAt(4, "}");
                }
                else
                {
                    code.AppendLineAt(4, "if (local." + field + ".Kind != " + kind + ".Unchanged)");
                    code.AppendLineAt(4, "{");
                    code.AppendLineAt(5, "var " + baseMember + " = baseFragment." + name + ";");
                    code.AppendLineAt(
                        5,
                        "var " + currentMember + " = currentFragment." + name + ";"
                    );
                    code.AppendLineAt(
                        5,
                        "var " + desiredMember + " = local." + field + ".Apply(" + baseMember + ");"
                    );
                    EmitScalarRebase(
                        code,
                        field,
                        member.Property.Name,
                        baseMember,
                        desiredMember,
                        currentMember,
                        equality,
                        scalarKind,
                        conflict,
                        5
                    );
                    code.AppendLineAt(4, "}");
                }
            }

            code.AppendLineAt(3, "}");
        }

        code.AppendLineAt(3, "return new " + rebaseResult + "(result, conflicts);");
        code.AppendLineAt(2, "}");
    }

    private static void EmitScalarRebase(
        SharedIndentedBuilder code,
        string field,
        string memberName,
        string baseMember,
        string desiredMember,
        string currentMember,
        string equality,
        string conflictKind,
        string conflict,
        int indent
    )
    {
        code.AppendLineAt(
            indent,
            "if (" + equality + "(" + baseMember + ", " + currentMember + "))"
        );
        code.AppendLineAt(indent, "{");
        code.AppendLineAt(indent + 1, "result." + field + " = local." + field + ";");
        code.AppendLineAt(indent, "}");
        code.AppendLineAt(
            indent,
            "else if (!"
                + equality
                + "("
                + desiredMember
                + ", "
                + currentMember
                + ") && !"
                + equality
                + "("
                + desiredMember
                + ", "
                + baseMember
                + "))"
        );
        code.AppendLineAt(indent, "{");
        code.AppendLineAt(
            indent + 1,
            "conflicts.Add(new "
                + conflict
                + "(new string[] { "
                + SymbolDisplay.FormatLiteral(memberName, true)
                + " }, "
                + conflictKind
                + ", __SparseMember("
                + baseMember
                + "), __SparseMember("
                + desiredMember
                + "), __SparseMember("
                + currentMember
                + "), \"The member conflicts with a concurrent change.\"));"
        );
        code.AppendLineAt(indent, "}");
    }
}
