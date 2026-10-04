using System.Collections.Immutable;
using System.Linq;

namespace SparseFragments.Generator.Shared;

/// <summary>Emits model/fragment conversion: <c>From</c>, <c>ToModel</c> and projection constructors.</summary>
internal sealed class SparseFragmentConversionEmitter
{
    private string Optional { get; }
    private string CloneContext { get; }
    private string ReferenceComparer { get; }
    private SparseFragmentExpressions Expressions { get; }

    public SparseFragmentConversionEmitter(
        string optional,
        string cloneContext,
        string referenceComparer,
        SparseFragmentExpressions expressions
    )
    {
        Optional = optional;
        CloneContext = cloneContext;
        ReferenceComparer = referenceComparer;
        Expressions = expressions;
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
            SparseFragmentEmitHelpers.AppendNullGuard(code, 3, "value");
        }

        SparseFragmentEmitHelpers.AppendCloneContext(code, 3, CloneContext, ReferenceComparer);
        code.AppendLineAt(
            3,
            "var __sparse_from_context = new global::System.Collections.Generic.List<object>();"
        );
        code.AppendLineAt(
            3,
            "return From(value, " + CloneContext + ", __sparse_from_context, \"\");"
        );
        code.AppendLineAt(2, "}");
        code.AppendLine();
        code.AppendIndent(2)
            .Append("internal static Fragment From(")
            .Append(modelType)
            .Append(" value, global::System.Collections.Generic.Dictionary<object, object> ")
            .Append(CloneContext)
            .Append(
                ", global::System.Collections.Generic.List<object> __sparse_from_context, string __sparse_from_path)"
            )
            .AppendLine("");
        code.AppendLineAt(2, "{");
        if (modelIsReferenceType)
        {
            SparseFragmentEmitHelpers.AppendNullGuard(code, 3, "value");
            code.AppendLineAt(
                3,
                "for (var __sparse_from_index = 0; __sparse_from_index < __sparse_from_context.Count; __sparse_from_index++)"
            );
            code.AppendLineAt(3, "{");
            code.AppendLineAt(
                4,
                "if (global::System.Object.ReferenceEquals(__sparse_from_context[__sparse_from_index], value))"
            );
            code.AppendLineAt(4, "{");
            code.AppendLineAt(
                5,
                "throw new global::System.NotSupportedException(\"Cyclic reference detected during From at '\" + __sparse_from_path + \"'. Fragment.From does not support cyclic object graphs; DeepClone preserves cycles.\");"
            );
            code.AppendLineAt(4, "}");
            code.AppendLineAt(3, "}");
            code.AppendLineAt(3, "__sparse_from_context.Add(value);");
            code.AppendLineAt(3, "try");
            code.AppendLineAt(3, "{");
            AppendFromModelBody(code, members, 4);
            code.AppendLineAt(3, "}");
            code.AppendLineAt(3, "finally");
            code.AppendLineAt(3, "{");
            code.AppendLineAt(
                4,
                "__sparse_from_context.RemoveAt(__sparse_from_context.Count - 1);"
            );
            code.AppendLineAt(3, "}");
        }
        else
        {
            AppendFromModelBody(code, members, 3);
        }
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private void AppendFromModelBody(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        int indent
    )
    {
        code.AppendLineAt(indent, "return new Fragment");
        code.AppendLineAt(indent, "{");
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
                value =
                    $"{member.ChildFragmentType}.From({access}, {CloneContext}, __sparse_from_context, {FromMemberPath(member)})";
            }
            else
            {
                value =
                    $"({access} is null ? null : {member.ChildFragmentType}.From({access}, {CloneContext}, __sparse_from_context, {FromMemberPath(member)}))";
            }

            code.AppendIndent(indent + 1)
                .Append(SparseNaming.EscapeIdentifier(member.Property.Name))
                .Append(" = ")
                .Append(Optional)
                .Append("<")
                .Append(SparseFragmentEmitHelpers.FragmentValueType(member))
                .Append(">.Present(")
                .Append(value)
                .AppendLine("),");
        }

        code.AppendLineAt(indent, "};");
    }

    private static string FromMemberPath(SparseMemberModel member)
    {
        var escaped = member.Property.Name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "(__sparse_from_path.Length == 0 ? \""
            + escaped
            + "\" : __sparse_from_path + \"."
            + escaped
            + "\")";
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
        if (constructor?.IsImplicitParameterlessClassConstructor == true)
            code.AppendLineAt(1, "public " + modelName + "() { }");
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
        code.AppendIndent(2)
            .Append("public ")
            .Append(modelType)
            .Append(" ToModel(")
            .Append(modelType)
            .AppendLine(" baseline)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "return From(baseline).Merge(this).ToModel();");
        code.AppendLineAt(2, "}");
        code.AppendLine();
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
}
