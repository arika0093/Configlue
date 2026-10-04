using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;

namespace SparseFragments.Generator.Shared;

/// <summary>Emits the standalone three-way rebase with structured conflicts.</summary>
internal static class SparseFragmentPatchRebaseEmitter
{
    private static string EqualMethod(SparseMemberModel member) => "__SparseEqual_" + member.Id;

    public static void AppendPatchRebase(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members
    )
    {
        var runtime = SparseFragmentPatchEmitter.Runtime;
        var contract = SparseFragmentPatchEmitter.Contract(modelType, "Fragment");
        var prefix = SparseNaming.PatchApiPrefix(
            members.Select(static member => member.Property.Name)
        );
        var optionalFragment = runtime + "Optional<Fragment?>";
        var kind = runtime + "FragmentOperationKind";
        var comparer = runtime + "SparseFragmentComparer";
        var conflict = "global::SparseFragments.SparsePatchConflict";
        var conflictKind = "global::SparseFragments.SparsePatchConflictKind";
        var conflictList =
            "global::System.Collections.Generic.List<global::SparseFragments.SparsePatchConflict>";
        var rebaseResult = "global::SparseFragments.RebaseResult<Patch>";

        AppendMemberEqualityHelpers(code, members, runtime, comparer);
        AppendRebaseStateHelpers(code, runtime);
        AppendRebaseHeader(code, contract, prefix, rebaseResult, optionalFragment, kind, comparer, conflict, conflictKind, conflictList);

        code.AppendLineAt(3, "var baseFragment = baseState.Value!;");
        code.AppendLineAt(3, "var currentFragment = currentState.Value!;");

        foreach (var member in members)
        {
            code.AppendLineAt(3, "{");
            if (member.ChildModel is not null)
                AppendNestedMemberRebase(code, member, comparer, conflict, conflictKind);
            else
                AppendScalarMemberRebase(code, member, kind, conflict, conflictKind);
            code.AppendLineAt(3, "}");
        }

        code.AppendLineAt(3, "return new " + rebaseResult + "(result, conflicts);");
        code.AppendLineAt(2, "}");
    }

    private static void AppendMemberEqualityHelpers(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        string runtime,
        string comparer
    )
    {
        foreach (var member in members)
        {
            var optionalMember =
                runtime + "Optional<" + SparseFragmentPatchEmitter.ValueType(member) + ">";
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
                ? SparseFragmentPatchEmitter.Expressions.ValueEqualityExpression(
                    member,
                    "left.Value",
                    "right.Value"
                )
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
    }

    private static void AppendRebaseStateHelpers(SharedIndentedBuilder code, string runtime)
    {
        code.AppendIndent(2)
            .Append(
                "private static "
                    + runtime
                    + "Optional<object?> __SparseState("
                    + runtime
                    + "Optional<Fragment?>"
                    + " state) => state.IsPresent ? "
                    + runtime
                    + "Optional<object?>.Present((object?)state.Value) : "
                    + runtime
                    + "Optional<object?>.Missing;"
            )
            .AppendLine();
        code.AppendIndent(2)
            .Append(
                "private static "
                    + runtime
                    + "Optional<object?> __SparseMember<T>("
                    + runtime
                    + "Optional<T> value) => value.IsPresent ? "
                    + runtime
                    + "Optional<object?>.Present((object?)value.Value) : "
                    + runtime
                    + "Optional<object?>.Missing;"
            )
            .AppendLine();
    }

    private static void AppendRebaseHeader(
        SharedIndentedBuilder code,
        string contract,
        string prefix,
        string rebaseResult,
        string optionalFragment,
        string kind,
        string comparer,
        string conflict,
        string conflictKind,
        string conflictList
    )
    {
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
    }

    private static void AppendNestedMemberRebase(
        SharedIndentedBuilder code,
        SparseMemberModel member,
        string comparer,
        string conflict,
        string conflictKind
    )
    {
        var name = SparseNaming.EscapeIdentifier(member.Property.Name);
        var field = SparseFragmentPatchEmitter.Field(member);
        const string baseMember = "baseMember";
        const string currentMember = "currentMember";
        const string desiredMember = "desiredMember";

        code.AppendLineAt(
            4,
            "if (local."
                + field
                + " is not null && !(("
                + SparseFragmentPatchEmitter.ChildContract(member)
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
                + SparseFragmentPatchEmitter.ChildContract(member)
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
                + SparseFragmentPatchEmitter.ChildPatch(member)
                + "."
                + member.ChildModel!.Value.PatchApiPrefix
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

    private static void AppendScalarMemberRebase(
        SharedIndentedBuilder code,
        SparseMemberModel member,
        string kind,
        string conflict,
        string conflictKind
    )
    {
        var runtime = SparseFragmentPatchEmitter.Runtime;
        var name = SparseNaming.EscapeIdentifier(member.Property.Name);
        var field = SparseFragmentPatchEmitter.Field(member);
        const string baseMember = "baseMember";
        const string currentMember = "currentMember";
        const string desiredMember = "desiredMember";
        var equality = EqualMethod(member);
        var operation = runtime + "FragmentOperation";
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
            AppendCustomStrategyMemberRebase(
                code,
                member,
                field,
                kind,
                baseMember,
                desiredMember,
                currentMember,
                equality,
                scalarKind,
                conflict,
                conflictKind,
                4
            );
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
                    + runtime
                    + "SparseCollectionRebase."
                    + (member.MergeMode == 2 ? "TryRebaseAppend" : "TryRebaseSetUnion")
                    + "(beforeValues, desiredValues, currentValues, (object? left, object? right) => "
                    + runtime
                    + "SparseValueComparer.AreEqual(left, right), out var rebasedValues, out var reason))"
            );
            code.AppendLineAt(6, "{");
            var operationType = operation + "<" + SparseFragmentPatchEmitter.ValueType(member) + ">";
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

    private static void AppendCustomStrategyMemberRebase(
        SharedIndentedBuilder code,
        SparseMemberModel member,
        string field,
        string kind,
        string baseMember,
        string desiredMember,
        string currentMember,
        string equality,
        string scalarKind,
        string conflict,
        string conflictKind,
        int indent
    )
    {
        var name = SparseNaming.EscapeIdentifier(member.Property.Name);
        var operationType =
            SparseFragmentPatchEmitter.Runtime
            + "FragmentOperation<"
            + SparseFragmentPatchEmitter.ValueType(member)
            + ">";
        code.AppendLineAt(indent, "if (local." + field + ".Kind == " + kind + ".Set)");
        code.AppendLineAt(indent, "{");
        code.AppendLineAt(indent + 1, "var " + baseMember + " = baseFragment." + name + ";");
        code.AppendLineAt(
            indent + 1,
            "var " + currentMember + " = currentFragment." + name + ";"
        );
        code.AppendLineAt(
            indent + 1,
            "var " + desiredMember + " = local." + field + ".Apply(" + baseMember + ");"
        );
        var strategyField =
            "Fragment." + SparseWellKnownNames.MergeStrategyFieldPrefix + member.Id;
        code.AppendLineAt(
            indent + 1,
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
        code.AppendLineAt(indent + 1, "{");
        code.AppendLineAt(
            indent + 2,
            "result." + field + " = " + operationType + ".Set(rebasedValue);"
        );
        code.AppendLineAt(indent + 1, "}");
        code.AppendLineAt(indent + 1, "else");
        code.AppendLineAt(indent + 1, "{");
        code.AppendLineAt(
            indent + 2,
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
        code.AppendLineAt(indent + 1, "}");
        code.AppendLineAt(indent, "}");
        code.AppendLineAt(
            indent,
            "else if (local." + field + ".Kind != " + kind + ".Unchanged)"
        );
        code.AppendLineAt(indent, "{");
        code.AppendLineAt(indent + 1, "var " + baseMember + " = baseFragment." + name + ";");
        code.AppendLineAt(
            indent + 1,
            "var " + currentMember + " = currentFragment." + name + ";"
        );
        code.AppendLineAt(
            indent + 1,
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
            indent + 1
        );
        code.AppendLineAt(indent, "}");
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
