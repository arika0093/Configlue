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
    private static string FragmentValueType(MemberModel member)
    {
        if (member.ChildModel is null)
        {
            return member.Property.Type.Name;
        }

        return member.ChildFragmentType + "?";
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
        member.ChildModel is null ? member.Property.Type.RuntimeName : member.ChildFragmentType!;

    private static string TypeName(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    private static string TypeName(TypeModel type) => type.Name;

    private static string NonNullableTypeName(ITypeSymbol type) =>
        type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(TypeFormat);

    private static string MergeModeName(int mode) =>
        SparseFragments.Generator.Shared.SparseNaming.MergeModeName(mode);

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
        SparseFragments.Generator.Shared.SparseNaming.EscapeIdentifier(identifier);

    private static string Sanitize(string identifier, CancellationToken cancellationToken) =>
        SparseFragments.Generator.Shared.SparseNaming.Sanitize(identifier, cancellationToken);

    private static string GetStableTypeHash(string value, CancellationToken cancellationToken) =>
        SparseFragments.Generator.Shared.SparseNaming.GetStableTypeHash(value, cancellationToken);

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
