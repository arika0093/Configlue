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

    public string MergeStrategyField(SparseMemberModel member) =>
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

    public static void AppendDeclaration(
        SharedIndentedBuilder code,
        string fragmentInterface,
        string deepCloneable,
        System.Action<SharedIndentedBuilder>? appendAttributes = null
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
        );
        code.AppendLineAt(1, "{");
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
                .Append(FragmentValueType(member))
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

    public static void AppendCollectionCloneHelpers(SharedIndentedBuilder code)
    {
        code.AppendLineAt(
            1,
            "private static global::System.Collections.Concurrent.BlockingCollection<T> __CloneBlockingCollection<T>(global::System.Collections.Concurrent.BlockingCollection<T> original, global::System.Collections.Generic.IEnumerable<T> items)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "var queue = new global::System.Collections.Concurrent.ConcurrentQueue<T>(items);"
        );
        code.AppendLineAt(2, "var clone = original.BoundedCapacity > 0");
        code.AppendLineAt(
            3,
            "? new global::System.Collections.Concurrent.BlockingCollection<T>(queue, original.BoundedCapacity)"
        );
        code.AppendLineAt(
            3,
            ": new global::System.Collections.Concurrent.BlockingCollection<T>(queue);"
        );
        code.AppendLineAt(2, "if (original.IsAddingCompleted)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "clone.CompleteAdding();");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "return clone;");
        code.AppendLineAt(1, "}");
    }

    public void AppendDeepClone(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool usesPocoCloning,
        ModelConstructorBinding? constructor = null
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendIndent(1).Append("public ").Append(modelType).AppendLine(" DeepClone()");
        code.AppendLineAt(1, "{");
        if (usesPocoCloning)
        {
            AppendCloneContext(code, 2);
        }

        var boundClones = new Dictionary<string, string>(StringComparer.Ordinal);
        if (constructor is not null)
        {
            foreach (
                var propertyName in constructor.Parameters.Select(static parameter =>
                    parameter.PropertyName
                )
            )
            {
                if (boundClones.ContainsKey(propertyName))
                    continue;
                var member = members.Single(candidate => candidate.Property.Name == propertyName);
                var local = "__constructor_clone_" + boundClones.Count;
                boundClones.Add(propertyName, local);
                code.AppendLineAt(
                    2,
                    "var "
                        + local
                        + " = "
                        + Expressions.CloneModelExpression(
                            member,
                            "this." + SparseNaming.EscapeIdentifier(propertyName)
                        )
                        + ";"
                );
            }
        }
        var arguments = constructor is null
            ? string.Empty
            : string.Join(
                ", ",
                constructor.Parameters.Select(parameter => boundClones[parameter.PropertyName])
            );
        code.AppendIndent(2)
            .Append("return new ")
            .Append(modelType)
            .Append("(")
            .Append(arguments)
            .AppendLine(")");
        code.AppendLineAt(1, "{");
        foreach (var member in members.Where(static member => !member.Property.IsReadOnly))
        {
            code.AppendIndent(2)
                .Append(SparseNaming.EscapeIdentifier(member.Property.Name))
                .Append(" = ")
                .Append(
                    boundClones.TryGetValue(member.Property.Name, out var cloned)
                        ? cloned
                        : Expressions.CloneModelExpression(
                            member,
                            "this." + SparseNaming.EscapeIdentifier(member.Property.Name)
                        )
                )
                .AppendLine(",");
        }

        code.AppendLineAt(1, "};");
        code.AppendLineAt(1, "}");
    }

    public void AppendPocoCloneHelper(
        SharedIndentedBuilder code,
        string typeName,
        string cloneHelperName,
        ImmutableArray<SparseMemberModel> members,
        ModelConstructorBinding? constructor = null
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendIndent(1)
            .Append("private static ")
            .Append(typeName)
            .Append(' ')
            .Append(cloneHelperName)
            .Append('(')
            .Append(typeName)
            .AppendLine(
                " value, global::System.Collections.Generic.Dictionary<object, object> "
                    + CloneContext
                    + ")"
            );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if ("
                + CloneContext
                + ".TryGetValue(value, out var existing)) { return ("
                + typeName
                + ")existing; }"
        );
        var boundClones = new Dictionary<string, string>(StringComparer.Ordinal);
        if (constructor is not null)
        {
            foreach (
                var propertyName in constructor.Parameters.Select(static parameter =>
                    parameter.PropertyName
                )
            )
            {
                if (boundClones.ContainsKey(propertyName))
                    continue;
                var member = members.Single(candidate => candidate.Property.Name == propertyName);
                var local = "__constructor_clone_" + boundClones.Count;
                boundClones.Add(propertyName, local);
                code.AppendLineAt(
                    2,
                    "var "
                        + local
                        + " = "
                        + Expressions.CloneModelExpression(
                            member,
                            "value." + SparseNaming.EscapeIdentifier(propertyName)
                        )
                        + ";"
                );
            }
        }
        var arguments = constructor is null
            ? string.Empty
            : string.Join(
                ", ",
                constructor.Parameters.Select(parameter => boundClones[parameter.PropertyName])
            );
        code.AppendIndent(2)
            .Append("var clone = new ")
            .Append(typeName)
            .Append("(")
            .Append(arguments)
            .AppendLine(");");
        code.AppendLineAt(2, CloneContext + ".Add(value, clone);");
        foreach (
            var member in members.Where(static member =>
                !member.Property.IsReadOnly && !member.Property.IsInitOnly
            )
        )
        {
            var memberName = SparseNaming.EscapeIdentifier(member.Property.Name);
            code.AppendIndent(2)
                .Append("clone.")
                .Append(memberName)
                .Append(" = ")
                .Append(
                    boundClones.TryGetValue(member.Property.Name, out var cloned)
                        ? cloned
                        : Expressions.CloneModelExpression(member, "value." + memberName)
                )
                .AppendLine(";");
        }

        code.AppendLineAt(2, "return clone;");
        code.AppendLineAt(1, "}");
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

    public static void AppendRootProjectionConstructor(
        SharedIndentedBuilder code,
        string modelName,
        ImmutableArray<SparseMemberModel> members,
        ModelConstructorBinding? constructor = null
    )
    {
        if (
            ModelConstructionPlan.ForMembers(members).CanOverlayAfterConstruction
            && (constructor is null || constructor.Parameters.IsEmpty)
        )
            return;
        code.AppendLineAt(1, "private readonly struct __SparseProjectionToken { }");
        if (members.Any(static member => member.Property.IsRequired))
            code.AppendLineAt(1, "[global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]");
        var arguments = constructor is null
            ? string.Empty
            : string.Join(
                ", ",
                constructor.Parameters.Select(parameter =>
                {
                    var member = members.Single(candidate =>
                        candidate.Property.Name == parameter.PropertyName
                    );
                    var access =
                        "__sparse_projection."
                        + SparseNaming.EscapeIdentifier(parameter.PropertyName);
                    var value = access + ".Value!";
                    if (member.ChildModel is not null)
                        value = member.ChildIsReferenceType
                            ? access + ".Value?.ToModel()!"
                            : access + ".Value!.ToModel()";
                    return access + ".IsPresent ? " + value + " : " + parameter.DefaultExpression;
                })
            );
        code.AppendLineAt(
            1,
            "private "
                + modelName
                + "(Fragment __sparse_projection, __SparseProjectionToken _) : this("
                + arguments
                + ")"
        );
        code.AppendLineAt(1, "{");
        foreach (var member in members.Where(static member => !member.Property.IsReadOnly))
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var access = "__sparse_projection." + name;
            var value = access + ".Value!";
            if (member.ChildModel is not null)
                value = member.ChildIsReferenceType
                    ? access + ".Value?.ToModel()!"
                    : access + ".Value!.ToModel()";
            if (member.Property.IsRequired)
                code.AppendLineAt(
                    2,
                    "this."
                        + name
                        + " = "
                        + access
                        + ".IsPresent ? "
                        + value
                        + " : this."
                        + name
                        + "!;"
                );
            else
                code.AppendLineAt(
                    2,
                    "if (" + access + ".IsPresent) this." + name + " = " + value + ";"
                );
        }
        code.AppendLineAt(1, "}");
    }

    public static void AppendToModel(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool hasRootProjectionConstructor = false,
        ModelConstructorBinding? constructor = null
    )
    {
        code.AppendIndent(2).Append("public ").Append(modelType).AppendLine(" ToModel()");
        code.AppendLineAt(2, "{");
        var construction = hasRootProjectionConstructor
            ? ModelConstructionPlan.ForMembers(members)
            : ModelConstructionPlan.ForStructuralMembers(members, constructor);
        if (
            hasRootProjectionConstructor
            && (
                !construction.CanOverlayAfterConstruction
                || (constructor is not null && !constructor.Parameters.IsEmpty)
            )
        )
        {
            code.AppendLineAt(
                3,
                "return new " + modelType + "(this, default(__SparseProjectionToken));"
            );
            code.AppendLineAt(2, "}");
            code.AppendLine();
            return;
        }
        if (construction.CanOverlayAfterConstruction)
        {
            var arguments = constructor is null
                ? string.Empty
                : string.Join(
                    ", ",
                    constructor.Parameters.Select(parameter =>
                    {
                        var member = members.Single(candidate =>
                            candidate.Property.Name == parameter.PropertyName
                        );
                        var name = SparseNaming.EscapeIdentifier(parameter.PropertyName);
                        var projected = name + ".Value!";
                        if (member.ChildModel is not null)
                            projected = member.ChildIsReferenceType
                                ? name + ".Value?.ToModel()!"
                                : name + ".Value!.ToModel()";
                        return name
                            + ".IsPresent ? "
                            + projected
                            + " : "
                            + parameter.DefaultExpression;
                    })
                );
            code.AppendLineAt(3, "var value = new " + modelType + "(" + arguments + ");");
            foreach (
                var member in members.Where(static member =>
                    !member.Property.IsReadOnly && !member.Property.IsInitOnly
                )
            )
            {
                var name = SparseNaming.EscapeIdentifier(member.Property.Name);
                var projected = name + ".Value!";
                if (member.ChildModel is not null)
                    projected = member.ChildIsReferenceType
                        ? name + ".Value?.ToModel()!"
                        : name + ".Value!.ToModel()";
                code.AppendLineAt(
                    3,
                    "if (" + name + ".IsPresent) value." + name + " = " + projected + ";"
                );
            }
            code.AppendLineAt(3, "return value;");
            code.AppendLineAt(2, "}");
            code.AppendLine();
            return;
        }
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
