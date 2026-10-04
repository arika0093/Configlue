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

    private const int JsonIgnoreNever = 0;
    private const int JsonIgnoreAlways = 1;

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

    private static int GetJsonIgnoreCondition(
        IPropertySymbol property,
        CancellationToken cancellationToken
    )
    {
        foreach (var attribute in property.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                attribute.AttributeClass?.ToDisplayString()
                != "System.Text.Json.Serialization.JsonIgnoreAttribute"
            )
            {
                continue;
            }

            // [JsonIgnore] without arguments means Always. Otherwise honor the
            // configured Condition, whether supplied as a named argument or as a
            // constructor argument.
            var condition = JsonIgnoreAlways;
            foreach (var argument in attribute.NamedArguments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    string.Equals(argument.Key, "Condition", StringComparison.Ordinal)
                    && TryParseJsonIgnoreCondition(argument.Value, out var namedCondition)
                )
                {
                    condition = namedCondition;
                }
            }

            if (attribute.ConstructorArguments.Length == 1)
            {
                var hasExplicitCondition = attribute.NamedArguments.Any(static argument =>
                    string.Equals(argument.Key, "Condition", StringComparison.Ordinal)
                );

                if (
                    !hasExplicitCondition
                    && TryParseJsonIgnoreCondition(
                        attribute.ConstructorArguments[0],
                        out var ctorCondition
                    )
                )
                {
                    condition = ctorCondition;
                }
            }

            return condition;
        }

        return JsonIgnoreNever;
    }

    private static bool TryParseJsonIgnoreCondition(TypedConstant constant, out int condition)
    {
        if (constant.Value is int intValue && intValue >= 0 && intValue <= 3)
        {
            condition = intValue;
            return true;
        }

        if (constant.Value is long longValue && longValue >= 0 && longValue <= 3)
        {
            condition = (int)longValue;
            return true;
        }

        if (constant.Value is short shortValue && shortValue >= 0 && shortValue <= 3)
        {
            condition = shortValue;
            return true;
        }

        if (constant.Value is byte byteValue && byteValue <= 3)
        {
            condition = byteValue;
            return true;
        }

        condition = JsonIgnoreAlways;
        return false;
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
