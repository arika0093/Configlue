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

        if (member.Collection.Kind == CollectionKind.Unsupported)
        {
            return access;
        }

        var elementModel = IsConfiglueModel(member.Collection.ElementType, cancellationToken);
        var enumerated = access;
        if (elementModel)
        {
            var elementType = TypeName(member.Collection.ElementType);
            enumerated =
                $"global::System.Linq.Enumerable.Select({access}, static item => item is null ? null : (({elementType})item).DeepClone())";
        }

        return member.Collection.Kind switch
        {
            CollectionKind.Array => $"global::System.Linq.Enumerable.ToArray({enumerated})",
            CollectionKind.List =>
                $"new global::System.Collections.Generic.List<{TypeName(member.Collection.ElementType)}>({enumerated})",
            CollectionKind.Set =>
                $"new global::System.Collections.Generic.HashSet<{TypeName(member.Collection.ElementType)}>({enumerated})",
            _ => access,
        };
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

        var elementModel =
            member.Collection.Kind != CollectionKind.Unsupported
            && IsConfiglueModel(member.Collection.ElementType, cancellationToken);
        if (member.Collection.Kind == CollectionKind.Unsupported)
        {
            return access;
        }

        var enumerated = access;
        if (elementModel)
        {
            var elementType = TypeName(member.Collection.ElementType);
            enumerated =
                $"global::System.Linq.Enumerable.Select({access}!, static item => item is null ? null : (({elementType})item).DeepClone())";
        }

        var cloned = member.Collection.Kind switch
        {
            CollectionKind.Array => $"global::System.Linq.Enumerable.ToArray({enumerated})",
            CollectionKind.List =>
                $"new global::System.Collections.Generic.List<{TypeName(member.Collection.ElementType)}>({enumerated})",
            CollectionKind.Set =>
                $"new global::System.Collections.Generic.HashSet<{TypeName(member.Collection.ElementType)}>({enumerated})",
            _ => access,
        };
        return $"(object?){access} is null ? default : {cloned}";
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

    private static string FragmentValueType(MemberModel member)
    {
        if (member.ChildModel is null)
        {
            return TypeName(member.Property.Type);
        }

        return NonNullableTypeName(member.ChildModel) + ".Fragment?";
    }

    private static string TypeName(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    private static string NonNullableTypeName(ITypeSymbol type) =>
        type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(TypeFormat);

    private static string MergeModeName(int mode) =>
        mode switch
        {
            1 => "Deep",
            2 => "Append",
            3 => "SetUnion",
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

            foreach (var argument in attribute.NamedArguments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (argument.Key == "Id")
                {
                    return argument.Value.Value as string ?? model.ToDisplayString();
                }
            }
        }

        return model.ToDisplayString();
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

    private static int GetModelVersion(INamedTypeSymbol model, CancellationToken cancellationToken)
    {
        foreach (var attribute in model.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass?.ToDisplayString() == ModelAttributeName)
            {
                return attribute.ConstructorArguments.FirstOrDefault().Value is int version
                    ? version
                    : 1;
            }
        }

        return 1;
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
