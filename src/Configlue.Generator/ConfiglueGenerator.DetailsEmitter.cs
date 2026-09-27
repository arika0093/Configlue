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
                + " value, global::Configlue.ConfiglueDetailsSnapshot snapshot)"
        );
        code.AppendLineAt(1, "    : this(value, snapshot, \"\")");
        code.AppendLineAt(1, "{");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "public Details("
                + modelType
                + " value, global::Configlue.ConfiglueDetailsSnapshot snapshot, string pathPrefix)"
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
        if (
            member.Collection.Kind != CollectionKind.Unsupported
            && member.Collection.ElementType is not null
        )
        {
            var elementType = TypeName(member.Collection.ElementType);
            code.AppendIndent(2)
                .Append("this.")
                .Append(name)
                .Append(" = value.")
                .Append(name)
                .Append(" is { } ")
                .Append(name)
                .Append("Value ? Collection<")
                .Append(elementType)
                .Append(">(snapshot, ToReadOnly<")
                .Append(elementType)
                .Append(">(")
                .Append(name)
                .Append("Value), pathPrefix, ")
                .Append(SymbolDisplay.FormatLiteral(member.Property.Name, true))
                .AppendLine(") : null;");
            return;
        }

        if (member.ChildModel is not null)
        {
            var childType = NonNullableTypeName(member.ChildModel);
            code.AppendIndent(2)
                .Append("this.")
                .Append(name)
                .Append(" = value.")
                .Append(name)
                .Append(" is { } ")
                .Append(name)
                .Append("Value ? new ")
                .Append(childType)
                .Append(".Details(")
                .Append(name)
                .Append("Value, snapshot, pathPrefix + ")
                .Append(SymbolDisplay.FormatLiteral(member.Property.Name + ".", true))
                .AppendLine(") : null;");
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
            .Append(SymbolDisplay.FormatLiteral(member.Property.Name, true))
            .AppendLine(");");
    }

    private static void AppendDetailsProperty(IndentedStringBuilder code, MemberModel member)
    {
        var name = EscapeIdentifier(member.Property.Name);
        if (
            member.Collection.Kind != CollectionKind.Unsupported
            && member.Collection.ElementType is not null
        )
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
                .Append(NonNullableTypeName(member.ChildModel))
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
            "private static global::Configlue.ConfigValueDetails<T> Leaf<T>(global::Configlue.ConfiglueDetailsSnapshot snapshot, T effective, string prefix, string memberName)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(2, "var segments = (prefix + memberName).Split('.');");
        code.AppendLineAt(2, "var fullPath = string.Join('.', segments);");
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
            "var present = fragment is not null && global::Configlue.ConfiglueDetailsSnapshot.TryGetPathValue(fragment, segments, out raw) && (raw is null || raw is T);"
        );
        code.AppendLineAt(3, "var status = snapshot.SourceStatuses[index];");
        code.AppendLineAt(
            3,
            "var state = present ? global::Configlue.ConfigSourceValueState.Present : MapStatus(status);"
        );
        code.AppendLineAt(3, "if (present && effectiveSource is null)");
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "effectiveSource = snapshot.Sources[index];");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(
            3,
            "values[index] = new global::Configlue.ConfigSourceValueDetails<T?>(snapshot.Sources[index], state, present ? (T?)raw : default);"
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
            "private static global::Configlue.ConfigCollectionDetails<E> Collection<E>(global::Configlue.ConfiglueDetailsSnapshot snapshot, global::System.Collections.Generic.IReadOnlyList<E> effective, string prefix, string memberName)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(2, "var segments = (prefix + memberName).Split('.');");
        code.AppendLineAt(2, "var fullPath = string.Join('.', segments);");
        code.AppendLineAt(
            2,
            "var values = new global::Configlue.ConfigSourceValueDetails<global::System.Collections.Generic.IReadOnlyList<E>?>[snapshot.Sources.Count];"
        );
        code.AppendLineAt(2, "global::Configlue.ConfigSourceDetails? effectiveSource = null;");
        code.AppendLineAt(2, "for (var index = 0; index < snapshot.Sources.Count; index++)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var fragment = snapshot.SourceFragments[index];");
        code.AppendLineAt(3, "object? raw = null;");
        code.AppendLineAt(
            3,
            "var present = fragment is not null && global::Configlue.ConfiglueDetailsSnapshot.TryGetPathValue(fragment, segments, out raw) && raw is global::System.Collections.IEnumerable sequence;"
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
        code.AppendLineAt(3, "if (present && list is not null && effectiveSource is null)");
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "effectiveSource = snapshot.Sources[index];");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(
            3,
            "values[index] = new global::Configlue.ConfigSourceValueDetails<global::System.Collections.Generic.IReadOnlyList<E>?>(snapshot.Sources[index], state, list);"
        );
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "var elements = snapshot.CollectionElements(fullPath).Select(element => new global::Configlue.ConfigCollectionElementDetails<E>(element.Index, (E?)element.Value, element.SourceIndices.Select(sourceIndex => new global::Configlue.ConfigSourceValueDetails<E?>(snapshot.Sources[sourceIndex], global::Configlue.ConfigSourceValueState.Present, (E?)element.Value)).ToArray())).ToArray();"
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
            "private static global::Configlue.ConfigSourceValueState MapStatus(global::Configlue.StateReadStatus status) => status switch"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "global::Configlue.StateReadStatus.Unavailable => global::Configlue.ConfigSourceValueState.Unavailable,"
        );
        code.AppendLineAt(
            2,
            "global::Configlue.StateReadStatus.Invalid => global::Configlue.ConfigSourceValueState.Invalid,"
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
            "if (options is not global::Configlue.IConfiglueOptions<" + modelType + "> advanced)"
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
