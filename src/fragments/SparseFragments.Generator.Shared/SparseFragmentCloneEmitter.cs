using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SparseFragments.Generator.Shared;

/// <summary>Emits deep-clone support: model <c>DeepClone</c>, POCO helpers and fragment cloning.</summary>
internal sealed class SparseFragmentCloneEmitter
{
    private string Optional { get; }
    private string CloneContext { get; }
    private string ReferenceComparer { get; }
    private SparseFragmentExpressions Expressions { get; }

    public SparseFragmentCloneEmitter(
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

    public void AppendDeepClone(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool usesPocoCloning,
        ModelConstructorBinding? constructor = null,
        bool modelIsReferenceType = true
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendIndent(1).Append("public ").Append(modelType).AppendLine(" DeepClone()");
        code.AppendLineAt(1, "{");
        SparseFragmentEmitHelpers.AppendCloneContext(code, 2, CloneContext, ReferenceComparer);
        code.AppendLineAt(2, "return DeepClone(" + CloneContext + ");");
        code.AppendLineAt(1, "}");
        code.AppendIndent(1)
            .Append("public ")
            .Append(modelType)
            .Append(" DeepClone(global::System.Collections.Generic.Dictionary<object, object> ")
            .Append(CloneContext)
            .AppendLine(")");
        code.AppendLineAt(1, "{");
        if (modelIsReferenceType)
            code.AppendLineAt(
                2,
                "if ("
                    + CloneContext
                    + ".TryGetValue(this, out var existing)) return ("
                    + modelType
                    + ")existing;"
            );
        if (
            modelIsReferenceType
            && (constructor is null || constructor.Parameters.IsEmpty)
            && ModelConstructionPlan.ForMembers(members).CanOverlayAfterConstruction
        )
        {
            code.AppendLineAt(2, "var clone = new " + modelType + "();");
            code.AppendLineAt(2, CloneContext + ".Add(this, clone);");
            foreach (var member in members.Where(static member => !member.Property.IsReadOnly))
            {
                var name = SparseNaming.EscapeIdentifier(member.Property.Name);
                code.AppendLineAt(
                    2,
                    "clone."
                        + name
                        + " = "
                        + Expressions.CloneModelExpression(member, "this." + name)
                        + ";"
                );
            }
            code.AppendLineAt(2, "return clone;");
            code.AppendLineAt(1, "}");
            return;
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
            .Append("var clone = new ")
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
        if (modelIsReferenceType)
            code.AppendLineAt(2, CloneContext + ".Add(this, clone);");
        code.AppendLineAt(2, "return clone;");
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
        SparseFragmentEmitHelpers.AppendCloneContext(code, 3, CloneContext, ReferenceComparer);
        code.AppendLineAt(3, "return DeepClone(" + CloneContext + ");");
        code.AppendLineAt(2, "}");
        code.AppendLine();
        code.AppendIndent(2)
            .Append(
                "internal Fragment DeepClone(global::System.Collections.Generic.Dictionary<object, object> "
            )
            .Append(CloneContext)
            .AppendLine(")");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if ("
                + CloneContext
                + ".TryGetValue(this, out var existing)) return (Fragment)existing;"
        );
        code.AppendLineAt(3, "return new Fragment(this, " + CloneContext + ");");
        code.AppendLineAt(2, "}");
        code.AppendLine();
        code.AppendIndent(2)
            .Append(
                "private Fragment(Fragment source, global::System.Collections.Generic.Dictionary<object, object> "
            )
            .Append(CloneContext)
            .AppendLine(")");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, CloneContext + ".Add(source, this);");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var type = SparseFragmentEmitHelpers.FragmentValueType(member);
            var expression = Expressions.CloneFragmentExpression(
                member,
                "source." + name + ".Value"
            );
            code.AppendIndent(4)
                .Append("this.")
                .Append(name)
                .Append(" = source.")
                .Append(name)
                .Append(".IsPresent ? ")
                .Append(Optional)
                .Append("<")
                .Append(type)
                .Append(">.Present(")
                .Append(expression)
                .AppendLine(") : default;");
        }

        code.AppendLineAt(2, "}");
        code.AppendLine();
    }
}
