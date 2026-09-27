using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    private static string CloneModelExpression(
        MemberModel member,
        string access,
        CancellationToken cancellationToken
    )
    {
        if (member.ChildModel is not null)
        {
            return $"{access} is null ? null! : {access}.DeepClone()";
        }

        var cloned = CloneCollectionExpression(member, access, cancellationToken);
        return member.Property.Type.IsReferenceType
            ? $"{access} is null ? null! : {cloned}"
            : cloned;
    }

    private static string CloneFragmentExpression(
        MemberModel member,
        string access,
        CancellationToken cancellationToken
    )
    {
        if (member.ChildModel is not null)
        {
            return $"{access}?.DeepClone()";
        }

        var cloned = CloneCollectionExpression(member, access + "!", cancellationToken);
        return $"(object?){access} is null ? default : {cloned}";
    }

    private static string CloneCollectionExpression(
        MemberModel member,
        string access,
        CancellationToken cancellationToken
    )
    {
        var collection = member.Collection;
        if (collection.CloneKind == CloneCollectionKind.Unsupported)
        {
            return access;
        }

        var elementType = TypeName(collection.ElementType);
        var elements = access;
        if (IsConfiglueModel(collection.ElementType, cancellationToken))
        {
            elements =
                $"global::System.Linq.Enumerable.Select({access}, static item => item is null ? default! : (({elementType})item).DeepClone())";
        }

        var valueType = collection.ValueType is null ? null : TypeName(collection.ValueType);
        if (collection.ValueType is not null)
        {
            if (collection.CloneKind == CloneCollectionKind.PriorityQueue)
            {
                var priorityType = TypeName(collection.ValueType);
                var elementSelector = IsConfiglueModel(collection.ElementType, cancellationToken)
                    ? $"item.Element is null ? default! : (({elementType})item.Element).DeepClone()"
                    : "item.Element";
                var prioritySelector = IsConfiglueModel(collection.ValueType, cancellationToken)
                    ? $"item.Priority is null ? default! : (({priorityType})item.Priority).DeepClone()"
                    : "item.Priority";
                var entries =
                    $"global::System.Linq.Enumerable.Select({access}.UnorderedItems, static item => ({elementSelector}, {prioritySelector}))";
                return $"new global::System.Collections.Generic.PriorityQueue<{elementType}, {priorityType}>({entries}, {access}.Comparer)";
            }

            if (collection.CloneKind == CloneCollectionKind.Dictionary)
            {
                var isConcreteDictionary =
                    collection.NamedType?.ConstructedFrom.ToDisplayString()
                    == "System.Collections.Generic.Dictionary<TKey, TValue>";
                var comparer = isConcreteDictionary ? access + ".Comparer" : null;
                var keySelector = IsConfiglueModel(collection.ElementType, cancellationToken)
                    ? $"static pair => pair.Key is null ? default! : (({elementType})pair.Key).DeepClone()"
                    : "static pair => pair.Key";
                var valueSelector = IsConfiglueModel(collection.ValueType, cancellationToken)
                    ? $"static pair => pair.Value is null ? default! : (({valueType})pair.Value).DeepClone()"
                    : "static pair => pair.Value";
                return comparer is null
                    ? $"global::System.Linq.Enumerable.ToDictionary({access}, {keySelector}, {valueSelector})"
                    : $"global::System.Linq.Enumerable.ToDictionary({access}, {keySelector}, {valueSelector}, {comparer})";
            }

            if (collection.CloneKind == CloneCollectionKind.ImmutableDictionary)
            {
                var keySelector = IsConfiglueModel(collection.ElementType, cancellationToken)
                    ? $"static pair => pair.Key is null ? default! : (({elementType})pair.Key).DeepClone()"
                    : "static pair => pair.Key";
                var valueSelector = IsConfiglueModel(collection.ValueType, cancellationToken)
                    ? $"static pair => pair.Value is null ? default! : (({valueType})pair.Value).DeepClone()"
                    : "static pair => pair.Value";
                return $"global::System.Collections.Immutable.ImmutableDictionary.ToImmutableDictionary({access}, {keySelector}, {valueSelector}, {access}.KeyComparer, {access}.ValueComparer)";
            }

            return access;
        }

        return collection.CloneKind switch
        {
            CloneCollectionKind.Array => $"global::System.Linq.Enumerable.ToArray({elements})",
            CloneCollectionKind.List =>
                $"new global::System.Collections.Generic.List<{elementType}>({elements})",
            CloneCollectionKind.Set => CloneSetExpression(
                collection,
                access,
                elements,
                elementType
            ),
            CloneCollectionKind.ImmutableSet => CloneSetExpression(
                collection,
                access,
                elements,
                elementType
            ),
            CloneCollectionKind.Queue =>
                $"new global::System.Collections.Generic.Queue<{elementType}>({elements})",
            CloneCollectionKind.Stack =>
                $"new global::System.Collections.Generic.Stack<{elementType}>(global::System.Linq.Enumerable.Reverse({elements}))",
            CloneCollectionKind.ConcurrentQueue =>
                $"new global::System.Collections.Concurrent.ConcurrentQueue<{elementType}>({elements})",
            CloneCollectionKind.ConcurrentStack =>
                $"new global::System.Collections.Concurrent.ConcurrentStack<{elementType}>(global::System.Linq.Enumerable.Reverse({elements}))",
            CloneCollectionKind.BlockingCollection =>
                $"__CloneBlockingCollection({access}, {elements})",
            CloneCollectionKind.LinkedList =>
                $"new global::System.Collections.Generic.LinkedList<{elementType}>({elements})",
            CloneCollectionKind.SortedSet =>
                $"new global::System.Collections.Generic.SortedSet<{elementType}>({elements}, {access}.Comparer)",
            CloneCollectionKind.ObservableCollection =>
                $"new global::System.Collections.ObjectModel.ObservableCollection<{elementType}>({elements})",
            CloneCollectionKind.ReadOnlyCollection =>
                $"new global::System.Collections.ObjectModel.ReadOnlyCollection<{elementType}>(new global::System.Collections.Generic.List<{elementType}>({elements}))",
            CloneCollectionKind.ImmutableArray =>
                $"{access}.IsDefault ? {access} : global::System.Collections.Immutable.ImmutableArray.CreateRange({elements})",
            CloneCollectionKind.ImmutableList =>
                $"global::System.Collections.Immutable.ImmutableList.CreateRange({elements})",
            _ => access,
        };
    }

    private static string CloneSetExpression(
        CollectionInfo collection,
        string access,
        string elements,
        string elementType
    )
    {
        var definition = collection.NamedType?.ConstructedFrom.ToDisplayString();
        return definition switch
        {
            "System.Collections.Generic.HashSet<T>" =>
                $"new global::System.Collections.Generic.HashSet<{elementType}>({elements}, {access}.Comparer)",
            "System.Collections.Generic.SortedSet<T>" =>
                $"new global::System.Collections.Generic.SortedSet<{elementType}>({elements}, {access}.Comparer)",
            "System.Collections.Immutable.ImmutableHashSet<T>" =>
                $"global::System.Collections.Immutable.ImmutableHashSet.CreateRange({access}.KeyComparer, {elements})",
            _ => $"new global::System.Collections.Generic.HashSet<{elementType}>({elements})",
        };
    }

    private static string BuildCollectionMerge(MemberModel member, string lower, string higher)
    {
        var elementType = TypeName(member.Collection.ElementType);
        var combined = $"global::System.Linq.Enumerable.Concat({lower}, {higher})";
        if (member.MergeMode == 3)
        {
            combined = $"global::System.Linq.Enumerable.Distinct({combined})";
        }

        return member.Collection.Kind switch
        {
            CollectionKind.List =>
                $"new global::System.Collections.Generic.List<{elementType}>({combined})",
            CollectionKind.Set =>
                $"new global::System.Collections.Generic.HashSet<{elementType}>({combined})",
            _ => $"global::System.Linq.Enumerable.ToArray({combined})",
        };
    }

    private static string MergeStrategyField(MemberModel member) =>
        "__configlue_merge_strategy_" + member.Id;

    private static string FragmentValueType(MemberModel member)
    {
        if (member.ChildModel is null)
        {
            return TypeName(member.Property.Type);
        }

        return NonNullableTypeName(member.ChildModel) + ".Fragment?";
    }

    private static string MemberBackingField(MemberModel member) =>
        "__configlue_member_" + member.Property.Name;

    private static bool HasPreviousVersion(
        INamedTypeSymbol current,
        INamedTypeSymbol previous,
        CancellationToken cancellationToken
    )
    {
        foreach (var attribute in current.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass?.ToDisplayString() != PreviousVersionAttributeName)
            {
                continue;
            }

            if (
                attribute.ConstructorArguments.FirstOrDefault().Value is INamedTypeSymbol target
                && SymbolEqualityComparer.Default.Equals(target, previous)
                && GetModelVersion(previous, cancellationToken)
                    < GetModelVersion(current, cancellationToken)
                && string.Equals(
                    GetModelId(previous, cancellationToken),
                    GetModelId(current, cancellationToken),
                    StringComparison.Ordinal
                )
            )
            {
                return true;
            }
        }

        return false;
    }

    private static string FragmentRuntimeValueType(MemberModel member) =>
        member.ChildModel is null
            ? member.Property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : NonNullableTypeName(member.ChildModel) + ".Fragment";

    private static string TypeName(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    private static string NonNullableTypeName(ITypeSymbol type) =>
        type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(TypeFormat);

    private static string MergeModeName(int mode) =>
        mode switch
        {
            1 => "Deep",
            2 => "Append",
            3 => "SetUnion",
            4 => "Custom",
            _ => "Replace",
        };

    private static string GetModelId(INamedTypeSymbol model, CancellationToken cancellationToken)
    {
        foreach (var attribute in model.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass?.ToDisplayString() != ModelAttributeName)
            {
                continue;
            }

            return attribute.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty;
        }

        return string.Empty;
    }

    private static string GetJsonPropertyName(
        IPropertySymbol property,
        CancellationToken cancellationToken,
        out bool isExplicit
    )
    {
        foreach (var attribute in property.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                attribute.AttributeClass?.ToDisplayString()
                    == "System.Text.Json.Serialization.JsonPropertyNameAttribute"
                && attribute.ConstructorArguments.FirstOrDefault().Value is string configuredName
            )
            {
                isExplicit = true;
                return configuredName;
            }
        }

        isExplicit = false;
        return property.Name;
    }

    private static string? GetEnvironmentVariableName(
        IPropertySymbol property,
        CancellationToken cancellationToken
    )
    {
        foreach (var attribute in property.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                attribute.AttributeClass?.ToDisplayString()
                    == "Configlue.ConfiglueEnvironmentAttribute"
                && attribute.ConstructorArguments.FirstOrDefault().Value is string name
            )
            {
                return name;
            }
        }

        return null;
    }

    private static int GetModelVersion(INamedTypeSymbol model, CancellationToken cancellationToken)
    {
        foreach (var attribute in model.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass?.ToDisplayString() != ModelAttributeName)
            {
                continue;
            }

            foreach (var argument in attribute.NamedArguments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (argument.Key == "Version" && argument.Value.Value is int version)
                {
                    return version;
                }
            }

            return InitialSchemaVersion;
        }

        return InitialSchemaVersion;
    }

    private static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None
        || SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None
            ? "@" + identifier
            : identifier;

    private static string Sanitize(string identifier, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(identifier.Length);
        foreach (var character in identifier)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    private static string GetStableTypeHash(string value, CancellationToken cancellationToken)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var character in value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash = unchecked((hash ^ character) * prime);
        }

        return hash.ToString("X8", CultureInfo.InvariantCulture);
    }

    private static string JoinMemberExpressions(
        ImmutableArray<MemberModel> members,
        Func<MemberModel, string> selector,
        CancellationToken cancellationToken
    )
    {
        var expressions = new List<string>(members.Length);
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            expressions.Add(selector(member));
        }

        return string.Join(" && ", expressions);
    }
}
