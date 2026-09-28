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
    private const int ReplaceMergeMode = 0;

    private static void AppendDetailsTree(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendLine("public sealed class Details");
        code.AppendLine("{");
        code.AppendLineAt(
            1,
            "public Details("
                + modelType
                + " value, global::Configlue.CompilerServices.ConfiglueDetailsSnapshot snapshot)"
        );
        code.AppendLineAt(
            1,
            "    : this(value, snapshot, global::Configlue.CompilerServices.ConfiglueMemberPath.Root(snapshot.Schema))"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "public Details("
                + modelType
                + " value, global::Configlue.CompilerServices.ConfiglueDetailsSnapshot snapshot, global::Configlue.CompilerServices.ConfiglueMemberPath pathPrefix)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(2, "global::System.ArgumentNullException.ThrowIfNull(snapshot);");
        foreach (var member in members)
        {
            AppendDetailsMember(code, member);
        }

        code.AppendLineAt(1, "}");
        foreach (var member in members)
        {
            AppendDetailsProperty(code, member);
        }

        AppendDetailsLeafHelper(code);
        AppendDetailsCollectionHelper(code);
        AppendDetailsStatusHelper(code);
        code.AppendLine("}");
    }

    private static void AppendDetailsMember(IndentedStringBuilder code, MemberModel member)
    {
        var name = EscapeIdentifier(member.Property.Name);
        if (member.Collection.Kind != CollectionKind.Unsupported)
        {
            var elementType = TypeName(member.Collection.ElementType);
            code.AppendIndent(2)
                .Append("this.")
                .Append(name)
                .Append(" = value.")
                .Append(name)
                .Append(" is not null ? Collection<")
                .Append(elementType)
                .Append(">(snapshot, ToReadOnly<")
                .Append(elementType)
                .Append(">(")
                .Append("value.")
                .Append(name)
                .Append("!), ")
                .Append(member.MergeMode == ReplaceMergeMode ? "true" : "false")
                .Append(", pathPrefix, ")
                .Append(member.Id)
                .AppendLine(") : null;");
            return;
        }

        if (member.ChildModel is not null)
        {
            var childType = member.ChildModel.Value.NonNullableName;
            code.AppendIndent(2)
                .Append("this.")
                .Append(name)
                .Append(" = value.")
                .Append(name)
                .Append(" is not null ? new ")
                .Append(childType)
                .Append(".Details(")
                .Append("value.")
                .Append(name)
                .Append("!, snapshot, pathPrefix.Append(")
                .Append(member.Id)
                .AppendLine(")) : null;");
            return;
        }

        var valueType = TypeName(member.Property.Type);
        code.AppendIndent(2)
            .Append("this.")
            .Append(name)
            .Append(" = Leaf<")
            .Append(valueType)
            .Append(">(snapshot, value.")
            .Append(name)
            .Append(", pathPrefix, ")
            .Append(member.Id)
            .AppendLine(");");
    }

    private static void AppendDetailsProperty(IndentedStringBuilder code, MemberModel member)
    {
        var name = EscapeIdentifier(member.Property.Name);
        if (member.Collection.Kind != CollectionKind.Unsupported)
        {
            code.AppendIndent(1)
                .Append("public global::Configlue.ConfigCollectionDetails<")
                .Append(TypeName(member.Collection.ElementType))
                .Append(">? ")
                .Append(name)
                .AppendLine(" { get; }");
            return;
        }

        if (member.ChildModel is not null)
        {
            code.AppendIndent(1)
                .Append("public ")
                .Append(member.ChildModel.Value.NonNullableName)
                .Append(".Details? ")
                .Append(name)
                .AppendLine(" { get; }");
            return;
        }

        code.AppendIndent(1)
            .Append("public global::Configlue.ConfigValueDetails<")
            .Append(TypeName(member.Property.Type))
            .Append("> ")
            .Append(name)
            .AppendLine(" { get; }");
    }

    private static void AppendDetailsLeafHelper(IndentedStringBuilder code)
    {
        code.AppendLineAt(
            1,
            "private static global::Configlue.ConfigValueDetails<T> Leaf<T>(global::Configlue.CompilerServices.ConfiglueDetailsSnapshot snapshot, T effective, global::Configlue.CompilerServices.ConfiglueMemberPath prefix, int memberId)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(2, "var fullPath = prefix.Append(memberId);");
        code.AppendLineAt(
            2,
            "var values = new global::Configlue.ConfigSourceValueDetails<T?>[snapshot.Sources.Count];"
        );
        code.AppendLineAt(2, "global::Configlue.ConfigSourceDetails? effectiveSource = null;");
        code.AppendLineAt(2, "for (var index = 0; index < snapshot.Sources.Count; index++)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var fragment = snapshot.SourceFragments[index];");
        code.AppendLineAt(3, "object? raw = null;");
        code.AppendLineAt(
            3,
            "var present = fragment is not null && fullPath.TryGetFragmentValue(fragment, out raw) && (raw is null || raw is T);"
        );
        code.AppendLineAt(3, "var status = snapshot.SourceStatuses[index];");
        code.AppendLineAt(
            3,
            "var state = present ? global::Configlue.ConfigSourceValueState.Present : MapStatus(status);"
        );
        code.AppendLineAt(3, "var isShadowed = present && effectiveSource is not null;");
        code.AppendLineAt(3, "if (present && effectiveSource is null)");
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "effectiveSource = snapshot.Sources[index];");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(
            3,
            "values[index] = new global::Configlue.ConfigSourceValueDetails<T?>(snapshot.Sources[index], state, present ? (T?)raw : default) { IsShadowed = isShadowed };"
        );
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "return new global::Configlue.ConfigValueDetails<T>(effective, snapshot.Editability(fullPath), effectiveSource, values);"
        );
        code.AppendLineAt(1, "}");
    }

    private static void AppendDetailsCollectionHelper(IndentedStringBuilder code)
    {
        code.AppendLineAt(
            1,
            "private static global::System.Collections.Generic.IReadOnlyList<E> ToReadOnly<E>(global::System.Collections.IEnumerable values)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (values is global::System.Collections.Generic.IReadOnlyList<E> list)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "return list;");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "return global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Cast<E>(values));"
        );
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static global::Configlue.ConfigCollectionDetails<E> Collection<E>(global::Configlue.CompilerServices.ConfiglueDetailsSnapshot snapshot, global::System.Collections.Generic.IReadOnlyList<E> effective, bool replaceSemantics, global::Configlue.CompilerServices.ConfiglueMemberPath prefix, int memberId)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(2, "var fullPath = prefix.Append(memberId);");
        code.AppendLineAt(
            2,
            "var values = new global::Configlue.ConfigSourceValueDetails<global::System.Collections.Generic.IReadOnlyList<E>?>[snapshot.Sources.Count];"
        );
        code.AppendLineAt(2, "var elementData = snapshot.CollectionElements(fullPath);");
        code.AppendLineAt(2, "global::Configlue.ConfigSourceDetails? effectiveSource = null;");
        code.AppendLineAt(2, "for (var index = 0; index < snapshot.Sources.Count; index++)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var fragment = snapshot.SourceFragments[index];");
        code.AppendLineAt(3, "object? raw = null;");
        code.AppendLineAt(
            3,
            "var present = fragment is not null && fullPath.TryGetFragmentValue(fragment, out raw) && raw is global::System.Collections.IEnumerable sequence;"
        );
        code.AppendLineAt(
            3,
            "global::System.Collections.Generic.IReadOnlyList<E>? list = present ? ToReadOnly<E>((global::System.Collections.IEnumerable)raw!) : null;"
        );
        code.AppendLineAt(3, "var status = snapshot.SourceStatuses[index];");
        code.AppendLineAt(
            3,
            "var state = present && list is not null ? global::Configlue.ConfigSourceValueState.Present : MapStatus(status);"
        );
        code.AppendLineAt(
            3,
            "var isShadowed = present && (replaceSemantics ? effectiveSource is not null : list is not null && list.Count > 0 && !global::System.Linq.Enumerable.Any(elementData, element => global::System.Linq.Enumerable.Contains(element.SourceIndices, index)));"
        );
        code.AppendLineAt(3, "if (present && list is not null && effectiveSource is null)");
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "effectiveSource = snapshot.Sources[index];");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(
            3,
            "values[index] = new global::Configlue.ConfigSourceValueDetails<global::System.Collections.Generic.IReadOnlyList<E>?>(snapshot.Sources[index], state, list) { IsShadowed = isShadowed };"
        );
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "if (!replaceSemantics)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "var elementSourceIndices = global::System.Linq.Enumerable.Take(global::System.Linq.Enumerable.Distinct(global::System.Linq.Enumerable.SelectMany(elementData, element => element.SourceIndices)), 2).ToArray();"
        );
        code.AppendLineAt(
            3,
            "effectiveSource = elementSourceIndices.Length == 1 ? snapshot.Sources[elementSourceIndices[0]] : null;"
        );
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "var elements = elementData.Select(element => new global::Configlue.ConfigCollectionElementDetails<E>(element.Index, (E?)element.Value, element.SourceIndices.Select(sourceIndex => new global::Configlue.ConfigSourceValueDetails<E?>(snapshot.Sources[sourceIndex], global::Configlue.ConfigSourceValueState.Present, (E?)element.Value)).ToArray())).ToArray();"
        );
        code.AppendLineAt(
            2,
            "return new global::Configlue.ConfigCollectionDetails<E>(effective, snapshot.Editability(fullPath), effectiveSource, values, elements);"
        );
        code.AppendLineAt(1, "}");
    }

    private static void AppendDetailsStatusHelper(IndentedStringBuilder code)
    {
        code.AppendLineAt(
            1,
            "private static global::Configlue.ConfigSourceValueState MapStatus(global::Configlue.State.StateReadStatus status) => status switch"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "global::Configlue.State.StateReadStatus.Unavailable => global::Configlue.ConfigSourceValueState.Unavailable,"
        );
        code.AppendLineAt(
            2,
            "global::Configlue.State.StateReadStatus.Invalid => global::Configlue.ConfigSourceValueState.Invalid,"
        );
        code.AppendLineAt(2, "_ => global::Configlue.ConfigSourceValueState.Missing,");
        code.AppendLineAt(1, "};");
    }

    private static void AppendDetailsExtensions(
        IndentedStringBuilder code,
        string modelType,
        string modelName
    )
    {
        var extensionType = modelName + "DetailsExtensions";
        code.AppendLine("public static class " + extensionType);
        code.AppendLine("{");
        code.AppendLineAt(
            1,
            "public static async global::System.Threading.Tasks.ValueTask<"
                + modelType
                + ".Details> GetDetailsAsync("
        );
        code.AppendLineAt(2, "this global::Configlue.IReadOnlyOptions<" + modelType + "> options,");
        code.AppendLineAt(
            2,
            "global::System.Threading.CancellationToken cancellationToken = default)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(2, "global::System.ArgumentNullException.ThrowIfNull(options);");
        code.AppendLineAt(
            2,
            "if (options is not global::Configlue.CompilerServices.IConfiglueDetailsRuntime advanced)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "throw new global::System.NotSupportedException(\"This options implementation does not expose details snapshots.\");"
        );
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "var snapshot = await advanced.GetDetailsSnapshotAsync(cancellationToken).ConfigureAwait(false);"
        );
        code.AppendLineAt(
            2,
            "return new " + modelType + ".Details((" + modelType + ")snapshot.Value!, snapshot);"
        );
        code.AppendLineAt(1, "}");
        code.AppendLine("}");
    }
}
