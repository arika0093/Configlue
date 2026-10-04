using System.Collections.Immutable;
using System.Linq;

namespace SparseFragments.Generator.Shared;

/// <summary>Emits merge/diff/apply helpers for generated fragments.</summary>
internal sealed class SparseFragmentMergeEmitter
{
    private string Optional { get; }
    private string MergeStrategyFieldPrefix { get; }
    private SparseFragmentExpressions Expressions { get; }

    public SparseFragmentMergeEmitter(
        string optional,
        string mergeStrategyFieldPrefix,
        SparseFragmentExpressions expressions
    )
    {
        Optional = optional;
        MergeStrategyFieldPrefix = mergeStrategyFieldPrefix;
        Expressions = expressions;
    }

    private string MergeStrategyField(SparseMemberModel member) =>
        SparseFragmentEmitHelpers.MergeStrategyField(MergeStrategyFieldPrefix, member);

    public void AppendMerge(SharedIndentedBuilder code, ImmutableArray<SparseMemberModel> members)
    {
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
        SparseFragmentEmitHelpers.AppendNullGuard(code, 3, "higherPriority");
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
                    .Append(SparseNaming.EscapeIdentifier(members[index].Property.Name))
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
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
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
                    $"{higher}.IsPresent ? {Optional}<{SparseFragmentEmitHelpers.FragmentValueType(member)}>.Present(({lower}.IsPresent && (object?){lower}.Value is not null && (object?){higher}.Value is not null) ? {lower}.Value!.Merge({higher}.Value!) : {higher}.Value) : {lower}";
            }
            else if (member.MergeMode is 2 or 3)
            {
                var merged = Expressions.BuildCollectionMerge(
                    member,
                    lower + ".Value!",
                    higher + ".Value!"
                );
                expression =
                    $"{higher}.IsPresent ? ({lower}.IsPresent && (object?){lower}.Value is not null && (object?){higher}.Value is not null ? {Optional}<{SparseFragmentEmitHelpers.FragmentValueType(member)}>.Present({merged}) : {higher}) : {lower}";
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

    public void AppendApplyChanges(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members
    )
    {
        code.AppendLineAt(
            2,
            "/// <summary>Applies a sparse semantic diff to this contribution.</summary>"
        );
        code.AppendLineAt(2, "public Fragment ApplyChanges(Fragment changes)");
        code.AppendLineAt(2, "{");
        SparseFragmentEmitHelpers.AppendNullGuard(code, 3, "changes");
        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var type = SparseFragmentEmitHelpers.FragmentValueType(member);
            var expression = member.ChildModel is null
                ? $"changes.{name}.IsPresent ? changes.{name} : this.{name}"
                : $"changes.{name}.IsPresent ? {Optional}<{type}>.Present((this.{name}.IsPresent && (object?)this.{name}.Value is not null && (object?)changes.{name}.Value is not null) ? this.{name}.Value!.ApplyChanges(changes.{name}.Value!) : changes.{name}.Value) : this.{name}";
            code.AppendIndent(4).Append(name).Append(" = ").Append(expression).AppendLine(",");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    public void AppendDiff(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool modelIsReferenceType
    )
    {
        const string diffContextType =
            "global::System.Collections.Generic.List<global::System.Collections.Generic.KeyValuePair<object, object>>";
        foreach (var member in members.Where(static member => member.ChildModel is not null))
        {
            var type = member.ChildModel!.Value.NonNullableName;
            var fragment = member.ChildFragmentType!;
            code.AppendIndent(2)
                .Append("private static ")
                .Append(Optional)
                .Append("<")
                .Append(SparseFragmentEmitHelpers.FragmentValueType(member))
                .Append("> __Diff_")
                .Append(member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('(');
            if (!member.ChildIsReferenceType)
            {
                code.Append(type)
                    .Append(" before, ")
                    .Append(type)
                    .Append(" after, ")
                    .Append(diffContextType)
                    .AppendLine(" __sparse_diff_context, string __sparse_diff_path)");
                code.AppendLineAt(2, "{");
                code.AppendIndent(3)
                    .Append("if (global::System.Collections.Generic.EqualityComparer<")
                    .Append(type)
                    .AppendLine(">.Default.Equals(before!, after!)) { return default; }");
                code.AppendIndent(3)
                    .Append("return ")
                    .Append(Optional)
                    .Append("<")
                    .Append(SparseFragmentEmitHelpers.FragmentValueType(member))
                    .Append(">.Present(")
                    .Append(fragment)
                    .AppendLine(
                        ".Diff(before, after, __sparse_diff_context, __sparse_diff_path));"
                    );
                code.AppendLineAt(2, "}");
                continue;
            }

            code.Append(type)
                .Append("? before, ")
                .Append(type)
                .Append("? after, ")
                .Append(diffContextType)
                .AppendLine(" __sparse_diff_context, string __sparse_diff_path)");
            code.AppendLineAt(2, "{");
            code.AppendLineAt(
                3,
                "if (global::System.Object.ReferenceEquals(before, after)) { return default; }"
            );
            code.AppendIndent(3)
                .Append("if (before is null || after is null) { return ")
                .Append(Optional)
                .Append("<")
                .Append(SparseFragmentEmitHelpers.FragmentValueType(member))
                .Append(">.Present(after is null ? null : ")
                .Append(fragment)
                .AppendLine(".From(after)); }");
            code.AppendIndent(3)
                .Append("var difference = ")
                .Append(fragment)
                .AppendLine(".Diff(before, after, __sparse_diff_context, __sparse_diff_path);");
            code.AppendIndent(3)
                .Append("return difference.IsEmpty ? default : ")
                .Append(Optional)
                .Append("<")
                .Append(SparseFragmentEmitHelpers.FragmentValueType(member))
                .AppendLine(">.Present(difference);");
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
            SparseFragmentEmitHelpers.AppendNullGuard(code, 3, "before");
            SparseFragmentEmitHelpers.AppendNullGuard(code, 3, "after");
        }

        code.AppendLineAt(3, "var __sparse_diff_context = new " + diffContextType + "();");
        code.AppendLineAt(3, "return Diff(before, after, __sparse_diff_context, \"\");");
        code.AppendLineAt(2, "}");
        code.AppendLine();
        code.AppendIndent(2)
            .Append("internal static Fragment Diff(")
            .Append(modelType)
            .Append(" before, ")
            .Append(modelType)
            .Append(" after, ")
            .Append(diffContextType)
            .AppendLine(" __sparse_diff_context, string __sparse_diff_path)");
        code.AppendLineAt(2, "{");
        if (modelIsReferenceType)
        {
            SparseFragmentEmitHelpers.AppendNullGuard(code, 3, "before");
            SparseFragmentEmitHelpers.AppendNullGuard(code, 3, "after");
            code.AppendLineAt(
                3,
                "if (global::System.Object.ReferenceEquals(before, after)) { return new Fragment(); }"
            );
            code.AppendLineAt(
                3,
                "for (var __sparse_diff_index = 0; __sparse_diff_index < __sparse_diff_context.Count; __sparse_diff_index++)"
            );
            code.AppendLineAt(3, "{");
            code.AppendLineAt(
                4,
                "var __sparse_diff_pair = __sparse_diff_context[__sparse_diff_index];"
            );
            code.AppendLineAt(
                4,
                "if (global::System.Object.ReferenceEquals(__sparse_diff_pair.Key, (object)before) && global::System.Object.ReferenceEquals(__sparse_diff_pair.Value, (object)after))"
            );
            code.AppendLineAt(4, "{");
            code.AppendLineAt(
                5,
                "throw new global::System.NotSupportedException(\"Cyclic reference detected during Diff at '\" + __sparse_diff_path + \"'. Fragment.Diff does not support cyclic object graphs; DeepClone preserves cycles.\");"
            );
            code.AppendLineAt(4, "}");
            code.AppendLineAt(3, "}");
            code.AppendLineAt(
                3,
                "__sparse_diff_context.Add(new global::System.Collections.Generic.KeyValuePair<object, object>(before, after));"
            );
            code.AppendLineAt(3, "try");
            code.AppendLineAt(3, "{");
            AppendDiffBody(code, members, 4);
            code.AppendLineAt(3, "}");
            code.AppendLineAt(3, "finally");
            code.AppendLineAt(3, "{");
            code.AppendLineAt(
                4,
                "__sparse_diff_context.RemoveAt(__sparse_diff_context.Count - 1);"
            );
            code.AppendLineAt(3, "}");
        }
        else
        {
            AppendDiffBody(code, members, 3);
        }
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private void AppendDiffBody(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        int indent
    )
    {
        code.AppendLineAt(indent, "return new Fragment");
        code.AppendLineAt(indent, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var before = "before." + name;
            var after = "after." + name;
            var valueType = SparseFragmentEmitHelpers.FragmentValueType(member);
            string condition;
            if (member.MergeStrategyType is not null)
            {
                condition =
                    $"{MergeStrategyField(member)}.AreEqual({before}, {after}) ? default : {Optional}<{valueType}>.Present({after})";
            }
            else if (member.ChildModel is null)
            {
                condition =
                    $"{Expressions.ValueEqualityExpression(member, before, after)} ? default : {Optional}<{valueType}>.Present({after})";
            }
            else
            {
                condition =
                    $"__Diff_{member.Id}({before}, {after}, __sparse_diff_context, {DiffMemberPath(member)})";
            }

            code.AppendIndent(indent + 1)
                .Append(name)
                .Append(" = ")
                .Append(condition)
                .AppendLine(",");
        }

        code.AppendLineAt(indent, "};");
    }

    private static string DiffMemberPath(SparseMemberModel member)
    {
        var escaped = member.Property.Name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "(__sparse_diff_path.Length == 0 ? \""
            + escaped
            + "\" : __sparse_diff_path + \"."
            + escaped
            + "\")";
    }
}
