using System.Collections.Immutable;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    private static void AppendFragment(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members,
        ImmutableArray<PreviousModelInfo> previousModels,
        bool hasJsonFragmentRegistry
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        if (hasJsonFragmentRegistry)
        {
            code.AppendLineAt(
                1,
                "[global::System.Text.Json.Serialization.JsonConverter(typeof(FragmentJsonConverter))]"
            );
        }

        code.AppendLineAt(
            1,
            "public sealed partial class Fragment : global::Configlue.IConfiglueFragment<Fragment>"
        );
        code.AppendLineAt(1, "{");
        if (hasJsonFragmentRegistry)
        {
            code.AppendLineAt(
                2,
                "public static global::System.Text.Json.Serialization.JsonConverter<Fragment> JsonConverter { get; } = new FragmentJsonConverter();"
            );
        }

        code.AppendLineAt(
            2,
            "public global::Configlue.ConfiglueModelSchema ConfiglueSchema => ConfiglueFragmentSchema;"
        );
        code.AppendLine();
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
                        .Append(".IsPresent ? global::SparseFragments.Optional<")
                        .Append(childValueType)
                        .Append(">.Present(")
                        .Append(previousAccess)
                        .Append(".Value is null ? null : ")
                        .Append(childType)
                        .Append(".FromPrevious(")
                        .Append(previousAccess)
                        .Append(".Value!)) : global::SparseFragments.Optional<")
                        .Append(childValueType)
                        .AppendLine(">.Missing,");
                }
            }

            code.AppendLineAt(3, "};");
            code.AppendLineAt(2, "}");
            code.AppendLine();
        }
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
