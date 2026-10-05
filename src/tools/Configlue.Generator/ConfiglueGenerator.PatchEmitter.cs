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
        FragmentCore.AppendBuilder(code, members.Select(ToSparseMember).ToImmutableArray());
    }

    private static void AppendPatch(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members,
        bool isRootModel = true
    )
    {
        var wholePatch = "global::Configlue.IConfiglueModelPatch<" + modelType + ">";
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            1,
            "/// <summary>A source-local set/unset patch for generated fragment members.</summary>"
        );
        code.AppendLineAt(
            1,
            "public sealed class Patch : global::Configlue.CompilerServices.IConfiglueRoutablePatch, global::Configlue.IConfiglueReplacementPatch, "
                + wholePatch
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "public global::Configlue.ConfiglueModelSchema Schema => ConfiglueSchema;"
        );
        SparseFragments.Generator.Shared.SparseFragmentPatchCoreEmitter.AppendPatchMembers(
            code,
            members.Select(ToSparseMember).ToImmutableArray(),
            "global::Configlue.",
            static member => "__configlue_member_" + member.Property.Name
        );
        var sparseMembers = members.Select(ToSparseMember).ToImmutableArray();
        var patchDialect =
            new SparseFragments.Generator.Shared.SparseFragmentPatchEmitter.SparsePatchDialect(
                "global::Configlue.",
                "__configlue_whole_operation",
                "__configlue_members_empty",
                static member => "__configlue_member_" + member.Property.Name,
                static member =>
                    "global::Configlue.IConfiglueModelPatch<"
                    + member.ChildModel!.Value.NonNullableName
                    + ">",
                "ApplyNested",
                false
            );
        SparseFragments.Generator.Shared.SparseFragmentPatchCoreEmitter.AppendPatchWholeOperations(
            code,
            modelType,
            wholePatch,
            "global::Configlue.IConfigluePatch",
            sparseMembers,
            patchDialect
        );
        SparseFragments.Generator.Shared.SparseFragmentPatchCoreEmitter.AppendPatchConstructor(
            code,
            sparseMembers,
            patchDialect
        );
        SparseFragments.Generator.Shared.SparseFragmentPatchCoreEmitter.AppendPatchApplyMembers(
            code,
            sparseMembers,
            patchDialect
        );
        SparseFragments.Generator.Shared.SparseFragmentPatchCoreEmitter.AppendPatchOptionalApply(
            code,
            sparseMembers,
            patchDialect,
            "internal global::Configlue.Optional<Fragment?> ApplyNested(global::Configlue.Optional<Fragment?> current)"
        );
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
                code.AppendLineAt(
                    5,
                    "((global::Configlue.IConfiglueModelPatch<"
                        + member.ChildModel.Value.NonNullableName
                        + ">)nested).Unset();"
                );
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
            "global::Configlue.IConfigluePatch global::Configlue.CompilerServices.IConfiglueDynamicMemberPatch.SelectMembers(global::System.ReadOnlySpan<int> memberIds) => SelectMembersCore(memberIds);"
        );
        code.AppendLineAt(
            2,
            "private global::Configlue.IConfigluePatch SelectMembersCore(global::System.ReadOnlySpan<int> memberIds)"
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
        SparseFragments.Generator.Shared.SparseJsonPatchEmitter.AppendConfiglueJsonBetween(
            code,
            sparseMembers,
            static sparse => "__configlue_member_" + sparse.Property.Name,
            static sparse =>
                sparse.ChildModel is null
                    ? sparse.Property.Type.Name
                    : sparse.ChildFragmentType + "?",
            static sparse =>
                sparse.ChildFragmentType!.Substring(
                    0,
                    sparse.ChildFragmentType.Length - "Fragment".Length
                ) + "Patch.__ConfiglueJsonBetween"
        );
        if (isRootModel)
        {
            var jsonPrefix = SparseFragments.Generator.Shared.SparseNaming.JsonPatchApiPrefix(
                members.Select(static member => member.Property.Name)
            );
            SparseFragments.Generator.Shared.SparseJsonPatchEmitter.AppendFragmentJsonHelpers(
                code,
                "global::Configlue",
                "global::Configlue.Optional"
            );
            // Single shared bridge: Configlue differs only in Between helper and apply expression.
            // JSON Patch interop stays an adapter over the semantic patch, not routing.
            // Configlue uses its own embedded runtime (global::Configlue.ConfiglueJsonPatch)
            // so the facade package boundary stays free of a SparseFragments dependency.
            SparseFragments.Generator.Shared.SparseJsonPatchEmitter.AppendFromJsonPatch(
                code,
                "global::Configlue",
                "ConfiglueJsonPatch",
                "global::Configlue.Optional",
                jsonPrefix,
                "__ConfiglueJsonBetween"
            );
            SparseFragments.Generator.Shared.SparseJsonPatchEmitter.AppendToJsonPatch(
                code,
                "global::Configlue",
                "ConfiglueJsonPatch",
                "global::Configlue.Optional",
                jsonPrefix,
                "ApplyNested(baseline)"
            );
        }
        code.AppendLineAt(1, "}");
    }

    private static void AppendPatchRouting(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            2,
            "global::System.Collections.Generic.IReadOnlyDictionary<global::Configlue.SourceId, global::Configlue.IConfigluePatch> global::Configlue.CompilerServices.IConfiglueRoutablePatch.Route(global::Configlue.StateWritePlan writePlan, global::Configlue.SourceId? fallbackSourceId)"
        );
        code.AppendLineAt(2, "{");
        AppendNullGuard(code, 3, "writePlan");
        code.AppendLineAt(
            3,
            "return RouteCore(global::Configlue.CompilerServices.ConfiglueWriteRouting.Bind(writePlan, ConfiglueSchema), fallbackSourceId, global::Configlue.CompilerServices.ConfiglueMemberPath.Root(ConfiglueSchema));"
        );
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "internal global::System.Collections.Generic.Dictionary<global::Configlue.SourceId, global::Configlue.IConfigluePatch> RouteCore(global::Configlue.StateWritePlan writePlan, global::Configlue.SourceId? fallbackSourceId, global::Configlue.CompilerServices.ConfiglueMemberPath propertyPrefix)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "var routed = new global::System.Collections.Generic.Dictionary<global::Configlue.SourceId, global::Configlue.IConfigluePatch>();"
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
            "if (propertyPrefix.Length > 0 && global::Configlue.CompilerServices.ConfiglueWriteRouting.HasRouteBelow(writePlan, propertyPrefix)) { throw new global::System.NotSupportedException($\"A whole nested patch for '{propertyPrefix}' cannot be split across child source routes.\"); }"
        );
        code.AppendLineAt(
            4,
            "var wholeSourceId = (propertyPrefix.Length == 0 ? fallbackSourceId : global::Configlue.CompilerServices.ConfiglueWriteRouting.Resolve(writePlan, propertyPrefix, fallbackSourceId)) ?? throw new global::System.InvalidOperationException($\"No write owner is configured for '{propertyPrefix}'.\");"
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
                "var " + pathVariable + " = propertyPrefix.Append(" + member.Id + ");"
            );
            if (member.ChildModel is not null)
            {
                code.AppendLineAt(
                    4,
                    "if ("
                        + field
                        + " is not null && !"
                        + field
                        + ".IsEmpty && global::Configlue.CompilerServices.ConfiglueWriteRouting.HasRouteBelow(writePlan, "
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
                    "var sourceId = global::Configlue.CompilerServices.ConfiglueWriteRouting.Resolve(writePlan, "
                        + pathVariable
                        + ", fallbackSourceId) ?? throw new global::System.InvalidOperationException($\"No write owner is configured for '{"
                        + pathVariable
                        + "}'.\");"
                );
                code.AppendLineAt(
                    5,
                    "MergeRoutedPatch(routed, sourceId, (Patch)SelectMembersCore(new[] { "
                        + member.Id
                        + " }));"
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
                    "var sourceId = global::Configlue.CompilerServices.ConfiglueWriteRouting.Resolve(writePlan, "
                        + pathVariable
                        + ", fallbackSourceId) ?? throw new global::System.InvalidOperationException($\"No write owner is configured for '{"
                        + pathVariable
                        + "}'.\");"
                );
                code.AppendLineAt(
                    5,
                    "MergeRoutedPatch(routed, sourceId, (Patch)SelectMembersCore(new[] { "
                        + member.Id
                        + " }));"
                );
                code.AppendLineAt(4, "}");
            }

            code.AppendLineAt(3, "}");
        }

        code.AppendLineAt(3, "return routed;");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "private static void MergeRoutedPatch(global::System.Collections.Generic.Dictionary<global::Configlue.SourceId, global::Configlue.IConfigluePatch> routed, global::Configlue.SourceId sourceId, Patch patch)"
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

    private static string NestedPatchType(MemberModel member) => member.ChildPatchType!;

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
