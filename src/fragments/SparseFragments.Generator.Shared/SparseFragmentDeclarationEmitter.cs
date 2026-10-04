using System.Collections.Immutable;
using System.Linq;

namespace SparseFragments.Generator.Shared;

/// <summary>Emits the generated declaration shell: fragment type, storage/members and builder API.</summary>
internal sealed class SparseFragmentDeclarationEmitter(
    string optional,
    string mergeStrategyFieldPrefix
)
{
    private string Optional { get; } = optional;
    private string MergeStrategyFieldPrefix { get; } = mergeStrategyFieldPrefix;

    public string MergeStrategyField(SparseMemberModel member) =>
        SparseFragmentEmitHelpers.MergeStrategyField(MergeStrategyFieldPrefix, member);

    public static void AppendDeclaration(
        SharedIndentedBuilder code,
        string fragmentInterface,
        string deepCloneable,
        System.Action<SharedIndentedBuilder>? appendAttributes = null,
        string? advancedInterface = null
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            1,
            "/// <summary>A sparse, presence-aware representation of this model.</summary>"
        );
        appendAttributes?.Invoke(code);
        code.AppendLineAt(
            1,
            "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]"
        );
        code.AppendLineAt(
            1,
            "public sealed class Fragment : "
                + fragmentInterface
                + "<Fragment>, "
                + deepCloneable
                + "<Fragment>"
                + (advancedInterface is null ? string.Empty : ", " + advancedInterface)
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(2, "public Fragment() { }");
        code.AppendLine();
    }

    public void AppendMembers(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        string mergeStrategy,
        System.Action<SharedIndentedBuilder>? appendMemberAttributes = null
    )
    {
        foreach (var member in members)
        {
            code.CancellationToken.ThrowIfCancellationRequested();
            appendMemberAttributes?.Invoke(code);
            code.AppendIndent(2)
                .Append("public ")
                .Append(Optional)
                .Append("<")
                .Append(SparseFragmentEmitHelpers.FragmentValueType(member))
                .Append("> ")
                .Append(SparseNaming.EscapeIdentifier(member.Property.Name))
                .AppendLine(" { get; init; }");
        }
        foreach (var member in members.Where(static member => member.MergeStrategyType is not null))
        {
            code.AppendIndent(2)
                .Append("internal static readonly ")
                .Append(mergeStrategy)
                .Append("<")
                .Append(member.Property.Type.Name)
                .Append("> ")
                .Append(MergeStrategyField(member))
                .Append(" = new ")
                .Append(member.MergeStrategyType!.Value.Name)
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
                    : string.Join(
                        " && ",
                        members.Select(member =>
                            "!" + SparseNaming.EscapeIdentifier(member.Property.Name) + ".IsPresent"
                        )
                    )
            )
            .AppendLine(";");
        code.AppendLine();
    }

    public void AppendBuilder(SharedIndentedBuilder code, ImmutableArray<SparseMemberModel> members)
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(1, "/// <summary>A mutable builder for a generated fragment.</summary>");
        code.AppendLineAt(1, "public sealed class FragmentBuilder");
        code.AppendLineAt(1, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var field = "__sparse_builder_member_" + member.Id;
            code.AppendIndent(2)
                .Append("private ")
                .Append(Optional)
                .Append("<")
                .Append(SparseFragmentEmitHelpers.FragmentValueType(member))
                .Append("> ")
                .Append(field)
                .AppendLine(";");
            code.AppendIndent(2)
                .Append("public ref ")
                .Append(Optional)
                .Append("<")
                .Append(SparseFragmentEmitHelpers.FragmentValueType(member))
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
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            code.AppendIndent(3).Append(name).Append(" = fragment.").Append(name).AppendLine(";");
        }

        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "public Fragment Build() => new()");
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            code.AppendIndent(3).Append(name).Append(" = ").Append(name).AppendLine(",");
        }

        code.AppendLineAt(2, "};");
        code.AppendLineAt(1, "}");
    }
}
