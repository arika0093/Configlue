using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    private static void AppendBuilder(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendLineAt(1, "/// <summary>A mutable builder for a generated fragment.</summary>");
        code.AppendLineAt(1, "public sealed class FragmentBuilder");
        code.AppendLineAt(1, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var field = MemberBackingField(member);
            code.AppendIndent(2)
                .Append("private global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append("> ")
                .Append(field)
                .AppendLine(";");
            code.AppendIndent(2)
                .Append("public ref global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append("> ")
                .Append(name)
                .Append(" => ref ")
                .Append(field)
                .AppendLine(";");
        }

        code.AppendLineAt(2, "public FragmentBuilder() { }");
        code.AppendLineAt(2, "internal FragmentBuilder(Fragment fragment)");
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(3).Append(name).Append(" = fragment.").Append(name).AppendLine(";");
        }

        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "public Fragment Build() => new()");
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(3).Append(name).Append(" = ").Append(name).AppendLine(",");
        }

        code.AppendLineAt(2, "};");
        code.AppendLineAt(1, "}");
    }

    private static void AppendPatch(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendLineAt(
            1,
            "/// <summary>A source-local set/unset patch for generated fragment members.</summary>"
        );
        code.AppendLineAt(
            1,
            "public sealed class Patch : global::Configlue.IConfiglueRoutablePatch, global::Configlue.IConfiglueReplacementPatch"
        );
        code.AppendLineAt(1, "{");
        code.AppendIndent(2)
            .Append("public global::Configlue.ConfiglueModelSchema Schema => ")
            .Append(modelType)
            .AppendLine(".ConfiglueSchema;");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var field = MemberBackingField(member);
            if (member.ChildModel is null)
            {
                code.AppendIndent(2)
                    .Append("private global::Configlue.FragmentOperation<")
                    .Append(FragmentValueType(member))
                    .Append("> ")
                    .Append(field)
                    .AppendLine(";");
                code.AppendIndent(2)
                    .Append("public ref global::Configlue.FragmentOperation<")
                    .Append(FragmentValueType(member))
                    .Append("> ")
                    .Append(name)
                    .Append(" => ref ")
                    .Append(field)
                    .AppendLine(";");
            }
            else
            {
                var nestedPatchType = NestedPatchType(member);
                code.AppendIndent(2)
                    .Append("private ")
                    .Append(nestedPatchType)
                    .Append("? ")
                    .Append(field)
                    .AppendLine(";");
                code.AppendLineAt(2, "public " + nestedPatchType + " " + name);
                code.AppendLineAt(2, "{");
                code.AppendLineAt(3, "get => " + field + " ??= new " + nestedPatchType + "();");
                code.AppendLineAt(3, "set => " + field + " = value;");
                code.AppendLineAt(2, "}");
            }
        }

        code.AppendLineAt(
            2,
            "private global::Configlue.FragmentOperation<Fragment?> __configlue_whole_operation;"
        );
        code.AppendLineAt(
            2,
            "public void Set("
                + modelType
                + " value) => __configlue_whole_operation = global::Configlue.FragmentOperation<Fragment?>.Set("
                + modelType
                + ".Fragment.From(value));"
        );
        code.AppendLineAt(
            2,
            "public void SetNull() => __configlue_whole_operation = global::Configlue.FragmentOperation<Fragment?>.Set(null);"
        );
        code.AppendLineAt(
            2,
            "public void Unset() => __configlue_whole_operation = global::Configlue.FragmentOperation<Fragment?>.Unset;"
        );
        code.AppendLineAt(
            2,
            "public static implicit operator Patch(global::Configlue.FragmentOperation<Fragment?> operation)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var patch = new Patch();");
        code.AppendLineAt(
            3,
            "if (operation.Kind == global::Configlue.FragmentOperationKind.Unset) { patch.Unset(); }"
        );
        code.AppendLineAt(
            3,
            "else if (operation.Kind == global::Configlue.FragmentOperationKind.Set)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(
            4,
            "patch.__configlue_whole_operation = global::Configlue.FragmentOperation<Fragment?>.Set(operation.Value);"
        );
        code.AppendLineAt(3, "}");
        code.AppendLineAt(3, "return patch;");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "public Patch() { }");
        code.AppendLineAt(2, "public Patch(Fragment fragment)");
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            if (member.ChildModel is null)
            {
                code.AppendIndent(3)
                    .Append(name)
                    .Append(" = fragment.")
                    .Append(name)
                    .Append(".IsPresent ? global::Configlue.FragmentOperation<")
                    .Append(FragmentValueType(member))
                    .Append(">.Set(fragment.")
                    .Append(name)
                    .AppendLine(".Value) : default;");
            }
            else
            {
                var nestedPatchType = NestedPatchType(member);
                code.AppendIndent(3).Append("if (fragment.").Append(name).AppendLine(".IsPresent)");
                code.AppendLineAt(3, "{");
                code.AppendIndent(4)
                    .Append("var nested = new ")
                    .Append(nestedPatchType)
                    .AppendLine("();");
                code.AppendIndent(4)
                    .Append("if (fragment.")
                    .Append(name)
                    .AppendLine(".Value is null) nested.SetNull();");
                code.AppendIndent(4)
                    .Append("else nested = new ")
                    .Append(nestedPatchType)
                    .Append("(fragment.")
                    .Append(name)
                    .AppendLine(".Value);");
                code.AppendIndent(4).Append(MemberBackingField(member)).AppendLine(" = nested;");
                code.AppendLineAt(3, "}");
            }
        }

        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "public bool IsEmpty => ");
        code.AppendIndent(3)
            .Append(
                "__configlue_whole_operation.Kind == global::Configlue.FragmentOperationKind.Unchanged && "
            )
            .Append(
                members.Length == 0
                    ? "true"
                    : JoinMemberExpressions(
                        members,
                        static member =>
                            member.ChildModel is null
                                ? EscapeIdentifier(member.Property.Name)
                                    + ".Kind == global::Configlue.FragmentOperationKind.Unchanged"
                                : "("
                                    + MemberBackingField(member)
                                    + " is null || "
                                    + MemberBackingField(member)
                                    + ".IsEmpty)",
                        code.CancellationToken
                    )
            )
            .AppendLine(";");
        code.AppendLineAt(
            2,
            "internal global::Configlue.Optional<Fragment?> ApplyNested(global::Configlue.Optional<Fragment?> current)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if (__configlue_whole_operation.Kind == global::Configlue.FragmentOperationKind.Unset) { current = global::Configlue.Optional<Fragment?>.Missing; }"
        );
        code.AppendLineAt(
            3,
            "else if (__configlue_whole_operation.Kind == global::Configlue.FragmentOperationKind.Set) { current = global::Configlue.Optional<Fragment?>.Present(__configlue_whole_operation.Value); }"
        );
        code.AppendLineAt(
            3,
            "if (" + NestedOperationsEmptyExpression(members) + ") { return current; }"
        );
        code.AppendLineAt(
            3,
            "var basis = current.IsPresent && current.Value is not null ? current.Value : new Fragment();"
        );
        code.AppendLineAt(
            3,
            "return global::Configlue.Optional<Fragment?>.Present(basis.Apply(this));"
        );
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "internal Patch ClonePatch()");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "var clone = new Patch { __configlue_whole_operation = __configlue_whole_operation };"
        );
        foreach (var member in members)
        {
            var field = MemberBackingField(member);
            if (member.ChildModel is null)
            {
                code.AppendIndent(3)
                    .Append("clone.")
                    .Append(field)
                    .Append(" = ")
                    .Append(field)
                    .AppendLine(";");
            }
            else
            {
                code.AppendIndent(3)
                    .Append("clone.")
                    .Append(field)
                    .Append(" = ")
                    .Append(field)
                    .Append(" is null ? null : ")
                    .Append(field)
                    .AppendLine(".ClonePatch();");
            }
        }
        code.AppendLineAt(3, "return clone;");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "public global::Configlue.IConfigluePatch WithUnspecifiedMembersUnset()"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var replacement = ClonePatch();");
        code.AppendLineAt(
            3,
            "if (replacement.__configlue_whole_operation.Kind == global::Configlue.FragmentOperationKind.Unchanged)"
        );
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var field = MemberBackingField(member);
            if (member.ChildModel is null)
            {
                code.AppendLineAt(
                    4,
                    "if (replacement."
                        + field
                        + ".Kind == global::Configlue.FragmentOperationKind.Unchanged) { replacement."
                        + field
                        + " = global::Configlue.FragmentOperation<"
                        + FragmentValueType(member)
                        + ">.Unset; }"
                );
            }
            else
            {
                var nestedPatchType = NestedPatchType(member);
                code.AppendLineAt(4, "if (replacement." + field + " is null)");
                code.AppendLineAt(4, "{");
                code.AppendLineAt(5, "var nested = new " + nestedPatchType + "();");
                code.AppendLineAt(5, "nested.Unset();");
                code.AppendLineAt(5, "replacement." + field + " = nested;");
                code.AppendLineAt(4, "}");
                code.AppendLineAt(4, "else");
                code.AppendLineAt(4, "{");
                code.AppendLineAt(
                    5,
                    "replacement."
                        + field
                        + " = ("
                        + nestedPatchType
                        + ")replacement."
                        + field
                        + ".WithUnspecifiedMembersUnset();"
                );
                code.AppendLineAt(4, "}");
            }
        }
        code.AppendLineAt(3, "}");
        code.AppendLineAt(3, "return replacement;");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "public global::Configlue.IConfiglueFragment Apply(global::Configlue.IConfiglueFragment fragment)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if (fragment is not Fragment typed) { throw new global::System.ArgumentException(\"The patch can only be applied to its generated fragment type.\", nameof(fragment)); }"
        );
        code.AppendLineAt(3, "return typed.Apply(this);");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "public global::Configlue.IConfigluePatch SelectMembers(global::System.ReadOnlySpan<int> memberIds)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var selected = new Patch();");
        foreach (var member in members)
        {
            var field = MemberBackingField(member);
            code.AppendLineAt(3, "{");
            code.AppendLineAt(
                4,
                "for (var memberIndex = 0; memberIndex < memberIds.Length; memberIndex++)"
            );
            code.AppendLineAt(4, "{");
            code.AppendLineAt(5, $"if (memberIds[memberIndex] == {member.Id})");
            code.AppendLineAt(5, "{");
            if (member.ChildModel is null)
            {
                code.AppendIndent(6)
                    .Append("selected.")
                    .Append(field)
                    .Append(" = ")
                    .Append(field)
                    .AppendLine(";");
            }
            else
            {
                code.AppendIndent(6)
                    .Append("selected.")
                    .Append(field)
                    .Append(" = ")
                    .Append(field)
                    .Append(" is null ? null : ")
                    .Append(field)
                    .AppendLine(".ClonePatch();");
            }
            code.AppendLineAt(6, "break;");
            code.AppendLineAt(5, "}");
            code.AppendLineAt(4, "}");
            code.AppendLineAt(3, "}");
        }
        code.AppendLineAt(3, "return selected;");
        code.AppendLineAt(2, "}");
        AppendPatchRouting(code, members);
        code.AppendLineAt(1, "}");
    }

    private static void AppendPatchRouting(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendLineAt(
            2,
            "public global::System.Collections.Generic.IReadOnlyDictionary<string, global::Configlue.IConfigluePatch> Route(global::Configlue.StateWritePlan writePlan, string? fallbackSourceId)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "global::System.ArgumentNullException.ThrowIfNull(writePlan);");
        code.AppendLineAt(3, "return RouteCore(writePlan, fallbackSourceId, string.Empty);");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "internal global::System.Collections.Generic.Dictionary<string, global::Configlue.IConfigluePatch> RouteCore(global::Configlue.StateWritePlan writePlan, string? fallbackSourceId, string propertyPrefix)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "var routed = new global::System.Collections.Generic.Dictionary<string, global::Configlue.IConfigluePatch>(global::System.StringComparer.Ordinal);"
        );
        code.AppendLineAt(
            3,
            "if (__configlue_whole_operation.Kind != global::Configlue.FragmentOperationKind.Unchanged)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(
            4,
            "if (!("
                + NestedOperationsEmptyExpression(members)
                + ")) { throw new global::System.NotSupportedException(\"A whole-model operation cannot be combined with member patches during source routing.\"); }"
        );
        code.AppendLineAt(
            4,
            "if (propertyPrefix.Length > 0 && writePlan.HasRouteBelow(propertyPrefix)) { throw new global::System.NotSupportedException($\"A whole nested patch for '{propertyPrefix}' cannot be split across child source routes.\"); }"
        );
        code.AppendLineAt(
            4,
            "var wholeSourceId = (propertyPrefix.Length == 0 ? fallbackSourceId : writePlan.ResolveSourceIdOrNull(propertyPrefix, fallbackSourceId)) ?? throw new global::System.InvalidOperationException($\"No write owner is configured for '{propertyPrefix}'.\");"
        );
        code.AppendLineAt(4, "routed.Add(wholeSourceId, ClonePatch());");
        code.AppendLineAt(4, "return routed;");
        code.AppendLineAt(3, "}");
        foreach (var member in members)
        {
            var field = MemberBackingField(member);
            var name = EscapeIdentifier(member.Property.Name);
            var pathVariable = "__configlue_path_" + member.Id;
            code.AppendLineAt(3, "{");
            code.AppendLineAt(
                4,
                "var "
                    + pathVariable
                    + " = propertyPrefix.Length == 0 ? "
                    + SymbolDisplay.FormatLiteral(member.Property.Name, true)
                    + " : propertyPrefix + \".\" + "
                    + SymbolDisplay.FormatLiteral(member.Property.Name, true)
                    + ";"
            );
            if (member.ChildModel is not null)
            {
                code.AppendLineAt(
                    4,
                    "if ("
                        + field
                        + " is not null && !"
                        + field
                        + ".IsEmpty && writePlan.HasRouteBelow("
                        + pathVariable
                        + "))"
                );
                code.AppendLineAt(4, "{");
                code.AppendLineAt(
                    5,
                    "foreach (var (sourceId, nestedPatch) in "
                        + field
                        + ".RouteCore(writePlan, fallbackSourceId, "
                        + pathVariable
                        + "))"
                );
                code.AppendLineAt(5, "{");
                code.AppendLineAt(6, "var parentPatch = new Patch();");
                code.AppendLineAt(
                    6,
                    "parentPatch." + field + " = (" + NestedPatchType(member) + ")nestedPatch;"
                );
                code.AppendLineAt(6, "MergeRoutedPatch(routed, sourceId, parentPatch);");
                code.AppendLineAt(5, "}");
                code.AppendLineAt(4, "}");
                code.AppendLineAt(
                    4,
                    "else if (" + field + " is not null && !" + field + ".IsEmpty)"
                );
                code.AppendLineAt(4, "{");
                code.AppendLineAt(
                    5,
                    "var sourceId = writePlan.ResolveSourceIdOrNull("
                        + pathVariable
                        + ", fallbackSourceId) ?? throw new global::System.InvalidOperationException($\"No write owner is configured for '{"
                        + pathVariable
                        + "}'.\");"
                );
                code.AppendLineAt(
                    5,
                    "MergeRoutedPatch(routed, sourceId, (Patch)SelectMembers([" + member.Id + "]));"
                );
                code.AppendLineAt(4, "}");
            }
            else
            {
                code.AppendLineAt(
                    4,
                    "if (" + name + ".Kind != global::Configlue.FragmentOperationKind.Unchanged)"
                );
                code.AppendLineAt(4, "{");
                code.AppendLineAt(
                    5,
                    "var sourceId = writePlan.ResolveSourceIdOrNull("
                        + pathVariable
                        + ", fallbackSourceId) ?? throw new global::System.InvalidOperationException($\"No write owner is configured for '{"
                        + pathVariable
                        + "}'.\");"
                );
                code.AppendLineAt(
                    5,
                    "MergeRoutedPatch(routed, sourceId, (Patch)SelectMembers([" + member.Id + "]));"
                );
                code.AppendLineAt(4, "}");
            }

            code.AppendLineAt(3, "}");
        }

        code.AppendLineAt(3, "return routed;");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "private static void MergeRoutedPatch(global::System.Collections.Generic.Dictionary<string, global::Configlue.IConfigluePatch> routed, string sourceId, Patch patch)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "routed[sourceId] = routed.TryGetValue(sourceId, out var current) ? ((Patch)current).MergePatch(patch) : patch;"
        );
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "internal Patch MergePatch(Patch other)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var merged = ClonePatch();");
        code.AppendLineAt(
            3,
            "if (other.__configlue_whole_operation.Kind != global::Configlue.FragmentOperationKind.Unchanged)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(
            4,
            "if (!merged.IsEmpty) { throw new global::System.InvalidOperationException(\"A whole-model operation cannot be combined with member patches.\"); }"
        );
        code.AppendLineAt(
            4,
            "merged.__configlue_whole_operation = other.__configlue_whole_operation;"
        );
        code.AppendLineAt(3, "}");
        foreach (var member in members)
        {
            var field = MemberBackingField(member);
            if (member.ChildModel is null)
            {
                code.AppendLineAt(
                    3,
                    "if (other."
                        + field
                        + ".Kind != global::Configlue.FragmentOperationKind.Unchanged) merged."
                        + field
                        + " = other."
                        + field
                        + ";"
                );
            }
            else
            {
                code.AppendLineAt(3, "if (other." + field + " is not null)");
                code.AppendLineAt(3, "{");
                code.AppendLineAt(
                    4,
                    "merged."
                        + field
                        + " = merged."
                        + field
                        + " is null ? other."
                        + field
                        + ".ClonePatch() : merged."
                        + field
                        + ".MergePatch(other."
                        + field
                        + ");"
                );
                code.AppendLineAt(3, "}");
            }
        }

        code.AppendLineAt(3, "return merged;");
        code.AppendLineAt(2, "}");
    }

    private static string NestedPatchType(MemberModel member) =>
        member.ChildModel!.Value.NonNullableName + ".Patch";

    private static string NestedOperationsEmptyExpression(ImmutableArray<MemberModel> members) =>
        members.Length == 0
            ? "true"
            : JoinMemberExpressions(
                members,
                static member =>
                    member.ChildModel is null
                        ? EscapeIdentifier(member.Property.Name)
                            + ".Kind == global::Configlue.FragmentOperationKind.Unchanged"
                        : "("
                            + MemberBackingField(member)
                            + " is null || "
                            + MemberBackingField(member)
                            + ".IsEmpty)",
                default
            );
}
