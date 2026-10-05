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
        bool hasJsonFragmentRegistry,
        bool hasMessagePackFragmentRegistry,
        bool portableSetView = false,
        bool isRootModel = true,
        SparseFragments.Generator.Shared.ModelConstructorBinding? constructor = null
    )
    {
        var coreMembers = members
            .Select(member => ToSparseMember(member, portableSetView))
            .ToImmutableArray();
        SparseFragments.Generator.Shared.SparseFragmentCoreEmitter.AppendDeclaration(
            code,
            "global::Configlue.IConfiglueFragment",
            "global::Configlue.IConfiglueDeepCloneable",
            static writer =>
                writer.AppendLineAt(
                    1,
                    "[global::System.Text.Json.Serialization.JsonConverter(typeof(FragmentJsonConverter))]"
                ),
            "global::Configlue.CompilerServices.IConfiglueOrdinalDynamicFragment"
        );
        code.AppendLineAt(
            2,
            "public static global::System.Text.Json.Serialization.JsonConverter<Fragment> JsonConverter { get; } = new FragmentJsonConverter();"
        );
        if (hasMessagePackFragmentRegistry)
        {
            code.AppendLineAt(
                2,
                "public static global::MessagePack.Formatters.IMessagePackFormatter<Fragment> MessagePackFormatter { get; } = new FragmentMessagePackFormatter();"
            );
        }
        code.AppendLine();
        FragmentCore.AppendMembers(
            code,
            coreMembers,
            "global::Configlue.ConfiglueMergeStrategy",
            static writer =>
                writer.AppendLineAt(
                    2,
                    "[global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]"
                )
        );
        AppendFragmentDescriptor(code, members);
        FragmentCore.AppendFromModel(
            code,
            modelType,
            coreMembers,
            modelIsReferenceType,
            usesPocoCloning
        );
        SparseFragments.Generator.Shared.SparseFragmentCoreEmitter.AppendToModel(
            code,
            modelType,
            coreMembers,
            isRootModel,
            constructor
        );
        FragmentCore.AppendMerge(code, coreMembers);
        FragmentCore.AppendApplyChanges(code, coreMembers);
        FragmentCore.AppendDiff(code, modelType, coreMembers, modelIsReferenceType);
        FragmentCore.AppendFragmentClone(code, coreMembers, usesPocoCloning);
        AppendPatchSupport(code);
        AppendJsonConverter(code, members, hasJsonFragmentRegistry);
        if (hasMessagePackFragmentRegistry)
        {
            AppendMessagePackFormatter(code, members);
        }
        AppendPreviousMappings(code, previousModels);
        code.AppendLineAt(1, "}");
        AppendBuilder(code, members);
        AppendPatch(code, modelType, members, isRootModel);
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
                .Append(previousModel.Model.IsPublic ? "public" : "internal")
                .Append(" static Fragment FromPrevious(")
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
        code.AppendLineAt(
            2,
            "public global::Configlue.ConfiglueModelSchema Schema => FragmentSchema;"
        );
        code.AppendLineAt(
            2,
            "int global::Configlue.CompilerServices.IConfiglueOrdinalDynamicFragment.PresentMemberCount"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "get");
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "var count = 0;");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(4).Append("if (").Append(name).AppendLine(".IsPresent) count++;");
        }

        code.AppendLineAt(4, "return count;");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "global::Configlue.ConfiglueFragmentMember global::Configlue.CompilerServices.IConfiglueOrdinalDynamicFragment.GetPresentMember(int index)"
        );
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(3).Append("if (").Append(name).AppendLine(".IsPresent)");
            code.AppendLineAt(3, "{");
            code.AppendLineAt(4, "if (index == 0)");
            code.AppendLineAt(4, "{");
            code.AppendIndent(5)
                .Append("return new(")
                .Append(member.Id)
                .Append(", ")
                .Append(SymbolDisplay.FormatLiteral(member.Property.Name, true))
                .Append(", ")
                .Append(name)
                .AppendLine(".Value);");
            code.AppendLineAt(4, "}");
            code.AppendLineAt(4, "index--;");
            code.AppendLineAt(3, "}");
        }

        code.AppendLineAt(
            3,
            "throw new global::System.ArgumentOutOfRangeException(nameof(index));"
        );
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "global::System.Collections.Generic.IEnumerable<global::Configlue.ConfiglueFragmentMember> global::Configlue.CompilerServices.IConfiglueDynamicFragment.EnumeratePresentMembers()"
        );
        code.AppendLineAt(2, "{");
        if (members.IsEmpty)
            code.AppendLineAt(3, "yield break;");
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
            "global::Configlue.IConfiglueFragment global::Configlue.CompilerServices.IConfiglueDynamicFragment.WithMember(int memberId, object? value)"
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
        if (!members.IsEmpty)
            code.AppendLineAt(3, "return builder.Build();");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "global::Configlue.IConfiglueFragment global::Configlue.CompilerServices.IConfiglueDynamicFragment.WithoutMember(int memberId)"
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
        if (!members.IsEmpty)
            code.AppendLineAt(3, "return builder.Build();");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendPatchSupport(IndentedStringBuilder code)
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            2,
            "/// <summary>Applies source-local set and unset operations to this fragment.</summary>"
        );
        code.AppendLineAt(2, "public Fragment Apply(Patch patch)");
        code.AppendLineAt(2, "{");
        AppendNullGuard(code, 3, "patch");
        code.AppendLineAt(3, "return patch.ApplyMembers(this);");
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
