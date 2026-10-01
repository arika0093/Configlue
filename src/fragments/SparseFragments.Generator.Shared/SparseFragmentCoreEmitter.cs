using System.Collections.Immutable;
using System.Linq;

namespace SparseFragments.Generator.Shared;

/// <summary>Product-neutral fragment algebra emission, configured with runtime names.</summary>
internal sealed class SparseFragmentCoreEmitter(
    string optional,
    string mergeStrategyFieldPrefix,
    string cloneContext,
    string referenceComparer,
    SparseFragmentExpressions expressions
)
{
    private string Optional { get; } = optional;
    private string MergeStrategyFieldPrefix { get; } = mergeStrategyFieldPrefix;
    private string CloneContext { get; } = cloneContext;
    private string ReferenceComparer { get; } = referenceComparer;
    private SparseFragmentExpressions Expressions { get; } = expressions;

    private string MergeStrategyField(SparseMemberModel member) =>
        MergeStrategyFieldPrefix + member.Id;

    private static string FragmentValueType(SparseMemberModel member) =>
        member.ChildModel is null ? member.Property.Type.Name : member.ChildFragmentType + "?";

    private void AppendCloneContext(SharedIndentedBuilder code, int indent) =>
        code.AppendLineAt(
            indent,
            "var "
                + CloneContext
                + " = new global::System.Collections.Generic.Dictionary<object, object>("
                + ReferenceComparer
                + ".Instance);"
        );

    private static void AppendNullGuard(SharedIndentedBuilder code, int indent, string variable)
    {
        code.AppendLineAt(indent, "if (" + variable + " is null)");
        code.AppendLineAt(indent, "{");
        code.AppendLineAt(
            indent + 1,
            "throw new global::System.ArgumentNullException(nameof(" + variable + "));"
        );
        code.AppendLineAt(indent, "}");
    }

    public void AppendFromModel(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool modelIsReferenceType,
        bool usesPocoCloning
    )
    {
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
            AppendCloneContext(code, 3);
        }

        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var access = "value." + SparseNaming.EscapeIdentifier(member.Property.Name);
            string value;
            if (member.ChildModel is null)
            {
                value = Expressions.CloneModelExpression(member, access);
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
                .Append(SparseNaming.EscapeIdentifier(member.Property.Name))
                .Append(" = ")
                .Append(Optional)
                .Append("<")
                .Append(FragmentValueType(member))
                .Append(">.Present(")
                .Append(value)
                .AppendLine("),");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    public static void AppendToModel(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members
    )
    {
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

                code.Append(SparseNaming.EscapeIdentifier(members[index].Property.Name))
                    .Append(".IsPresent");
            }

            code.AppendLine(")");
            code.AppendLineAt(3, "{");
            AppendModelInitializer(code, modelType, members, 4, false);
            code.AppendLineAt(3, "}");
        }

        code.AppendIndent(3).Append("var defaults = new ").Append(modelType).AppendLine("();");
        AppendModelInitializer(code, modelType, members, 3, true);
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendModelInitializer(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        int indent,
        bool useDefaults
    )
    {
        code.AppendIndent(indent).Append("return new ").Append(modelType).AppendLine();
        code.AppendLineAt(indent, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
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
                    $"{higher}.IsPresent ? {Optional}<{FragmentValueType(member)}>.Present(({lower}.IsPresent && (object?){lower}.Value is not null && (object?){higher}.Value is not null) ? {lower}.Value!.Merge({higher}.Value!) : {higher}.Value) : {lower}";
            }
            else if (member.MergeMode is 2 or 3)
            {
                var merged = Expressions.BuildCollectionMerge(
                    member,
                    lower + ".Value!",
                    higher + ".Value!"
                );
                expression =
                    $"{higher}.IsPresent ? ({lower}.IsPresent && (object?){lower}.Value is not null && (object?){higher}.Value is not null ? {Optional}<{FragmentValueType(member)}>.Present({merged}) : {higher}) : {lower}";
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
        AppendNullGuard(code, 3, "changes");
        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var type = FragmentValueType(member);
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
        foreach (var member in members.Where(static member => member.ChildModel is not null))
        {
            var type = member.ChildModel!.Value.NonNullableName;
            var fragment = member.ChildFragmentType!;
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            code.AppendIndent(2)
                .Append("private static ")
                .Append(Optional)
                .Append("<")
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
                    .Append("return ")
                    .Append(Optional)
                    .Append("<")
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
                .Append("if (before is null || after is null) { return ")
                .Append(Optional)
                .Append("<")
                .Append(FragmentValueType(member))
                .Append(">.Present(after is null ? null : ")
                .Append(fragment)
                .AppendLine(".From(after)); }");
            code.AppendIndent(3)
                .Append("var difference = ")
                .Append(fragment)
                .AppendLine(".Diff(before, after);");
            code.AppendIndent(3)
                .Append("return difference.IsEmpty ? default : ")
                .Append(Optional)
                .Append("<")
                .Append(FragmentValueType(member))
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
            AppendNullGuard(code, 3, "before");
            AppendNullGuard(code, 3, "after");
        }

        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var before = "before." + name;
            var after = "after." + name;
            var valueType = FragmentValueType(member);
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
                condition = $"__Diff_{name}({before}, {after})";
            }

            code.AppendIndent(4).Append(name).Append(" = ").Append(condition).AppendLine(",");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    public void AppendFragmentClone(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        bool usesPocoCloning
    )
    {
        code.AppendLineAt(
            2,
            "/// <summary>Copies the fragment and its generated nested values.</summary>"
        );
        code.AppendLineAt(2, "public Fragment DeepClone()");
        code.AppendLineAt(2, "{");
        if (usesPocoCloning)
        {
            AppendCloneContext(code, 3);
        }

        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var type = FragmentValueType(member);
            var expression = Expressions.CloneFragmentExpression(member, "this." + name + ".Value");
            code.AppendIndent(4)
                .Append(name)
                .Append(" = this.")
                .Append(name)
                .Append(".IsPresent ? ")
                .Append(Optional)
                .Append("<")
                .Append(type)
                .Append(">.Present(")
                .Append(expression)
                .AppendLine(" ) : default,");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }
}
