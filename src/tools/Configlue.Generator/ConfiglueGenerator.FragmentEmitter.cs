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
    private static void AppendFragment(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members,
        ImmutableArray<PreviousModelInfo> previousModels,
        bool modelIsReferenceType,
        bool usesPocoCloning,
        bool hasJsonFragmentRegistry
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            1,
            "/// <summary>A sparse, presence-aware representation of this model.</summary>"
        );
        if (hasJsonFragmentRegistry)
        {
            code.AppendLineAt(
                1,
                "[global::System.Text.Json.Serialization.JsonConverter(typeof(FragmentJsonConverter))]"
            );
        }
        code.AppendLineAt(
            1,
            "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]"
        );
        code.AppendLineAt(
            1,
            "public sealed class Fragment : global::Configlue.IConfiglueFragment<Fragment>, global::Configlue.IConfiglueDeepCloneable<Fragment>"
        );
        code.AppendLineAt(1, "{");
        if (hasJsonFragmentRegistry)
        {
            code.AppendLineAt(
                2,
                "public static global::System.Text.Json.Serialization.JsonConverter<Fragment> JsonConverter { get; } = new FragmentJsonConverter();"
            );
        }
        code.AppendLine();
        foreach (var member in members)
        {
            if (hasJsonFragmentRegistry)
            {
                code.AppendLineAt(
                    2,
                    "[global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]"
                );
            }
            code.AppendIndent(2)
                .Append("public global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append("> ")
                .Append(EscapeIdentifier(member.Property.Name))
                .AppendLine(" { get; init; }");
        }

        foreach (var member in members.Where(static member => member.MergeStrategyType is not null))
        {
            code.AppendIndent(2)
                .Append("internal static readonly global::Configlue.ConfiglueMergeStrategy<")
                .Append(TypeName(member.Property.Type))
                .Append("> ")
                .Append("__configlue_merge_strategy_")
                .Append(member.Id)
                .Append(" = new ")
                .Append(TypeName(member.MergeStrategyType!.Value))
                .AppendLine("();");
        }

        code.AppendLine();
        code.AppendLineAt(
            2,
            "/// <summary>Whether this fragment has no present members.</summary>"
        );
        code.AppendIndent(2)
            .Append("public bool IsEmpty => ")
            .Append(
                members.Length == 0
                    ? "true"
                    : JoinMemberExpressions(
                        members,
                        static member =>
                            "!" + EscapeIdentifier(member.Property.Name) + ".IsPresent",
                        code.CancellationToken
                    )
            )
            .AppendLine(";");
        code.AppendLine();
        AppendFragmentDescriptor(code, members);
        AppendFromModel(code, modelType, members, modelIsReferenceType, usesPocoCloning);
        AppendToModel(code, modelType, members);
        AppendMerge(code, members);
        AppendApplyChanges(code, members);
        AppendDiff(code, modelType, members, modelIsReferenceType);
        AppendFragmentClone(code, members, usesPocoCloning);
        AppendPatchSupport(code, members);
        if (hasJsonFragmentRegistry)
        {
            AppendJsonConverter(code, members);
        }
        AppendPreviousMappings(code, previousModels);
        code.AppendLineAt(1, "}");
        AppendBuilder(code, members);
        AppendPatch(code, modelType, members);
    }

    private static void AppendPreviousMappings(
        IndentedStringBuilder code,
        ImmutableArray<PreviousModelInfo> previousModels
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        foreach (var previousModel in previousModels)
        {
            code.AppendLineAt(
                2,
                "/// <summary>Transfers compatible members from a declared previous schema version.</summary>"
            );
            code.AppendIndent(2)
                .Append("public static Fragment FromPrevious(")
                .Append(previousModel.Model.ModelTypeName)
                .AppendLine(".Fragment value)");
            code.AppendLineAt(2, "{");
            AppendNullGuard(code, 3, "value");
            code.AppendLineAt(3, "return new Fragment");
            code.AppendLineAt(3, "{");
            foreach (var mapping in previousModel.Mappings)
            {
                var member = mapping.CurrentMember;
                var previousMember = mapping.PreviousMember;
                var name = EscapeIdentifier(member.Property.Name);
                var previousName = EscapeIdentifier(previousMember.Property.Name);
                if (mapping.HasSameType)
                {
                    code.AppendIndent(4)
                        .Append(name)
                        .Append(" = value.")
                        .Append(previousName)
                        .AppendLine(",");
                    continue;
                }

                if (mapping.CanMigrateChild)
                {
                    var childType = member.ChildFragmentType!;
                    var childValueType = childType + "?";
                    var previousAccess = "value." + previousName;
                    code.AppendIndent(4)
                        .Append(name)
                        .Append(" = ")
                        .Append(previousAccess)
                        .Append(".IsPresent ? global::Configlue.Optional<")
                        .Append(childValueType)
                        .Append(">.Present(")
                        .Append(previousAccess)
                        .Append(".Value is null ? null : ")
                        .Append(childType)
                        .Append(".FromPrevious(")
                        .Append(previousAccess)
                        .Append(".Value!)) : global::Configlue.Optional<")
                        .Append(childValueType)
                        .AppendLine(">.Missing,");
                }
            }

            code.AppendLineAt(3, "};");
            code.AppendLineAt(2, "}");
            code.AppendLine();
        }
    }

    private static void AppendFragmentDescriptor(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(2, "public static Fragment Empty { get; } = new();");
        code.AppendIndent(2)
            .Append("public global::Configlue.ConfiglueModelSchema Schema => FragmentSchema;");
        code.AppendLineAt(
            2,
            "public global::System.Collections.Generic.IEnumerable<global::Configlue.ConfiglueFragmentMember> EnumeratePresentMembers()"
        );
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(3)
                .Append("if (")
                .Append(name)
                .Append(".IsPresent) yield return new(")
                .Append(member.Id)
                .Append(", ")
                .Append(SymbolDisplay.FormatLiteral(member.Property.Name, true))
                .Append(", ")
                .Append(name)
                .AppendLine(".Value);");
        }

        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "public global::Configlue.IConfiglueFragment WithMember(int memberId, object? value)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var builder = ToBuilder();");
        code.AppendLineAt(3, "switch (memberId)");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(4)
                .Append("case ")
                .Append(member.Id)
                .Append(": builder.")
                .Append(name)
                .Append(" = global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append(">.Present((")
                .Append(FragmentValueType(member))
                .AppendLine(")value!); break;");
        }

        code.AppendLineAt(
            4,
            "default: throw new global::System.ArgumentOutOfRangeException(nameof(memberId));"
        );
        code.AppendLineAt(3, "}");
        code.AppendLineAt(3, "return builder.Build();");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "public global::Configlue.IConfiglueFragment WithoutMember(int memberId)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var builder = ToBuilder();");
        code.AppendLineAt(3, "switch (memberId)");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            code.AppendIndent(4)
                .Append("case ")
                .Append(member.Id)
                .Append(": builder.")
                .Append(EscapeIdentifier(member.Property.Name))
                .Append(" = global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .AppendLine(">.Missing; break;");
        }

        code.AppendLineAt(
            4,
            "default: throw new global::System.ArgumentOutOfRangeException(nameof(memberId));"
        );
        code.AppendLineAt(3, "}");
        code.AppendLineAt(3, "return builder.Build();");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendFromModel(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members,
        bool modelIsReferenceType,
        bool usesPocoCloning
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendIndent(2)
            .Append("public static Fragment From(")
            .Append(modelType)
            .AppendLine(" value)");
        code.AppendLineAt(2, "{");
        if (modelIsReferenceType)
        {
            AppendNullGuard(code, 3, "value");
        }
        if (usesPocoCloning)
        {
            code.AppendLineAt(
                3,
                "var __configlue_clone_context = new global::System.Collections.Generic.Dictionary<object, object>(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);"
            );
        }
        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var access = "value." + EscapeIdentifier(member.Property.Name);
            string value;
            if (member.ChildModel is null)
            {
                value = CloneModelExpression(member, access, code.CancellationToken);
            }
            else if (!member.ChildIsReferenceType)
            {
                value = $"{member.ChildFragmentType}.From({access})";
            }
            else
            {
                value = $"({access} is null ? null : {member.ChildFragmentType}.From({access}))";
            }
            code.AppendIndent(4)
                .Append(EscapeIdentifier(member.Property.Name))
                .Append(" = global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append(">.Present(")
                .Append(value)
                .AppendLine("),");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendToModel(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendIndent(2).Append("public ").Append(modelType).AppendLine(" ToModel()");
        code.AppendLineAt(2, "{");
        if (!members.IsEmpty)
        {
            code.AppendIndent(3).Append("if (");
            for (var index = 0; index < members.Length; index++)
            {
                if (index > 0)
                {
                    code.Append(" && ");
                }

                code.Append(EscapeIdentifier(members[index].Property.Name)).Append(".IsPresent");
            }

            code.AppendLine(")");
            code.AppendLineAt(3, "{");
            AppendModelInitializer(code, modelType, members, indent: 4, useDefaults: false);
            code.AppendLineAt(3, "}");
        }

        code.AppendIndent(3).Append("var defaults = new ").Append(modelType).AppendLine("();");
        AppendModelInitializer(code, modelType, members, indent: 3, useDefaults: true);
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendModelInitializer(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members,
        int indent,
        bool useDefaults
    )
    {
        code.AppendIndent(indent).Append("return new ").Append(modelType).AppendLine();
        code.AppendLineAt(indent, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            string value;
            if (member.ChildModel is null)
            {
                value = name + ".Value!";
            }
            else if (!member.ChildIsReferenceType)
            {
                value = name + ".Value!.ToModel()";
            }
            else
            {
                value = name + ".Value?.ToModel()!";
            }
            code.AppendIndent(indent + 1)
                .Append(name)
                .Append(" = ")
                .Append(
                    useDefaults ? name + ".IsPresent ? " + value + " : defaults." + name : value
                )
                .AppendLine(",");
        }

        code.AppendLineAt(indent, "};");
    }

    private static void AppendMerge(IndentedStringBuilder code, ImmutableArray<MemberModel> members)
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        var hasCustomMergeStrategy = false;
        var replaceOnly = members.Length > 0;
        foreach (var member in members)
        {
            if (member.MergeStrategyType is not null)
            {
                hasCustomMergeStrategy = true;
                replaceOnly = false;
                continue;
            }

            if (
                (member.MergeMode == 1 && member.ChildModel is not null)
                || member.MergeMode is 2 or 3
            )
            {
                replaceOnly = false;
            }
        }

        code.AppendLineAt(
            2,
            "/// <summary>Merges a higher-priority fragment over this fragment.</summary>"
        );
        code.AppendLineAt(2, "public Fragment Merge(Fragment higherPriority)");
        code.AppendLineAt(2, "{");
        AppendNullGuard(code, 3, "higherPriority");
        if (!hasCustomMergeStrategy)
        {
            code.AppendLineAt(3, "if (higherPriority.IsEmpty)");
            code.AppendLineAt(3, "{");
            code.AppendLineAt(4, "return this;");
            code.AppendLineAt(3, "}");
            code.AppendLineAt(3, "if (IsEmpty)");
            code.AppendLineAt(3, "{");
            code.AppendLineAt(4, "return higherPriority;");
            code.AppendLineAt(3, "}");
        }

        if (replaceOnly)
        {
            code.AppendIndent(3).Append("if (");
            for (var index = 0; index < members.Length; index++)
            {
                if (index > 0)
                {
                    code.Append(" && ");
                }

                code.Append("higherPriority.")
                    .Append(EscapeIdentifier(members[index].Property.Name))
                    .Append(".IsPresent");
            }

            code.AppendLine(")");
            code.AppendLineAt(3, "{");
            code.AppendLineAt(4, "return higherPriority;");
            code.AppendLineAt(3, "}");
        }

        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var lower = "this." + name;
            var higher = "higherPriority." + name;
            string expression;
            if (member.MergeStrategyType is not null)
            {
                expression = $"{MergeStrategyField(member)}.Merge({lower}, {higher})";
            }
            else if (member.MergeMode == 1 && member.ChildModel is not null)
            {
                expression =
                    $"{higher}.IsPresent ? global::Configlue.Optional<{FragmentValueType(member)}>.Present(({lower}.IsPresent && (object?){lower}.Value is not null && (object?){higher}.Value is not null) ? {lower}.Value!.Merge({higher}.Value!) : {higher}.Value) : {lower}";
            }
            else if (member.MergeMode is 2 or 3)
            {
                var merged = BuildCollectionMerge(member, lower + ".Value!", higher + ".Value!");
                expression =
                    $"{higher}.IsPresent ? ({lower}.IsPresent && (object?){lower}.Value is not null && (object?){higher}.Value is not null ? global::Configlue.Optional<{FragmentValueType(member)}>.Present({merged}) : {higher}) : {lower}";
            }
            else
            {
                expression = $"{higher}.IsPresent ? {higher} : {lower}";
            }

            code.AppendIndent(4).Append(name).Append(" = ").Append(expression).AppendLine(",");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendApplyChanges(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            2,
            "/// <summary>Applies a sparse semantic diff to this source-local contribution.</summary>"
        );
        code.AppendLineAt(2, "public Fragment ApplyChanges(Fragment changes)");
        code.AppendLineAt(2, "{");
        AppendNullGuard(code, 3, "changes");
        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var type = FragmentValueType(member);
            var expression = member.ChildModel is null
                ? $"changes.{name}.IsPresent ? changes.{name} : this.{name}"
                : $"changes.{name}.IsPresent ? global::Configlue.Optional<{type}>.Present((this.{name}.IsPresent && (object?)this.{name}.Value is not null && (object?)changes.{name}.Value is not null) ? this.{name}.Value!.ApplyChanges(changes.{name}.Value!) : changes.{name}.Value) : this.{name}";
            code.AppendIndent(4).Append(name).Append(" = ").Append(expression).AppendLine(",");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendDiff(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members,
        bool modelIsReferenceType
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        foreach (var member in members.Where(static member => member.ChildModel is not null))
        {
            var type = member.ChildModel!.Value.NonNullableName;
            var fragment = member.ChildFragmentType!;
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(2)
                .Append("private static global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append("> __Diff_")
                .Append(name)
                .Append('(');
            if (!member.ChildIsReferenceType)
            {
                code.Append(type).Append(" before, ").Append(type).AppendLine(" after)");
                code.AppendLineAt(2, "{");
                code.AppendIndent(3)
                    .Append("if (global::System.Collections.Generic.EqualityComparer<")
                    .Append(type)
                    .AppendLine(">.Default.Equals(before, after)) { return default; }");
                code.AppendIndent(3)
                    .Append("return global::Configlue.Optional<")
                    .Append(FragmentValueType(member))
                    .Append(">.Present(")
                    .Append(fragment)
                    .AppendLine(".Diff(before, after));");
                code.AppendLineAt(2, "}");
                continue;
            }

            code.Append(type).Append("? before, ").Append(type).AppendLine("? after)");
            code.AppendLineAt(2, "{");
            code.AppendLineAt(
                3,
                "if (global::System.Object.ReferenceEquals(before, after)) { return default; }"
            );
            code.AppendIndent(3)
                .Append("if (before is null || after is null) { return global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append(">.Present(after is null ? null : ")
                .Append(fragment)
                .AppendLine(".From(after)); }");
            code.AppendIndent(3)
                .Append("var difference = ")
                .Append(fragment)
                .AppendLine(".Diff(before, after);");
            code.AppendIndent(3)
                .Append("return difference.IsEmpty ? default : global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .AppendLine(">.Present(difference); ");
            code.AppendLineAt(2, "}");
        }

        code.AppendLineAt(
            2,
            "/// <summary>Creates a sparse semantic diff between two ordinary model values.</summary>"
        );
        code.AppendIndent(2)
            .Append("public static Fragment Diff(")
            .Append(modelType)
            .Append(" before, ")
            .Append(modelType)
            .AppendLine(" after)");
        code.AppendLineAt(2, "{");
        if (modelIsReferenceType)
        {
            AppendNullGuard(code, 3, "before");
            AppendNullGuard(code, 3, "after");
        }

        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var before = "before." + name;
            var after = "after." + name;
            var valueType = FragmentValueType(member);
            string condition;
            if (member.MergeStrategyType is not null)
            {
                condition =
                    $"{MergeStrategyField(member)}.AreEqual({before}, {after}) ? default : global::Configlue.Optional<{valueType}>.Present({after})";
            }
            else if (member.ChildModel is null)
            {
                condition =
                    $"global::Configlue.ConfiglueValueComparer.AreEqual({before}, {after}) ? default : global::Configlue.Optional<{valueType}>.Present({after})";
            }
            else
            {
                condition = $"__Diff_{name}({before}, {after})";
            }
            code.AppendIndent(4).Append(name).Append(" = ").Append(condition).AppendLine(",");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendFragmentClone(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members,
        bool usesPocoCloning
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            2,
            "/// <summary>Copies the fragment and its generated nested values.</summary>"
        );
        code.AppendLineAt(2, "public Fragment DeepClone()");
        code.AppendLineAt(2, "{");
        if (usesPocoCloning)
        {
            code.AppendLineAt(
                3,
                "var __configlue_clone_context = new global::System.Collections.Generic.Dictionary<object, object>(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);"
            );
        }
        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var type = FragmentValueType(member);
            var expression = CloneFragmentExpression(
                member,
                "this." + name + ".Value",
                code.CancellationToken
            );
            code.AppendIndent(4)
                .Append(name)
                .Append(" = this.")
                .Append(name)
                .Append(".IsPresent ? global::Configlue.Optional<")
                .Append(type)
                .Append(">.Present(")
                .Append(expression)
                .Append(" ) : default,")
                .AppendLine();
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendPatchSupport(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            2,
            "/// <summary>Applies source-local set and unset operations to this fragment.</summary>"
        );
        code.AppendLineAt(2, "public Fragment Apply(Patch patch)");
        code.AppendLineAt(2, "{");
        AppendNullGuard(code, 3, "patch");
        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(4).Append(name).Append(" = ");
            if (member.ChildModel is null)
            {
                code.Append("patch.")
                    .Append(name)
                    .Append(".Apply(this.")
                    .Append(name)
                    .AppendLine("),");
            }
            else
            {
                code.Append("patch.")
                    .Append(name)
                    .Append(".ApplyNested(this.")
                    .Append(name)
                    .AppendLine("),");
            }
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
        code.AppendLineAt(
            2,
            "/// <summary>Creates a mutable builder initialized from this fragment.</summary>"
        );
        code.AppendLineAt(2, "public FragmentBuilder ToBuilder() => new(this);");
        code.AppendLine();
        code.AppendLineAt(
            2,
            "/// <summary>Creates a source-local set patch from all present members.</summary>"
        );
        code.AppendLineAt(2, "public Patch ToPatch() => new(this);");
    }
}
