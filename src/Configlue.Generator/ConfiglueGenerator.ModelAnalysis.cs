using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    private static bool IsConfiglueModel(ITypeSymbol type, CancellationToken cancellationToken)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class } named)
        {
            return false;
        }

        return HasConfiglueModelAttribute(named, cancellationToken);
    }

    private static bool TryGetPocoCloneType(
        ITypeSymbol type,
        CancellationToken cancellationToken,
        out INamedTypeSymbol pocoType
    )
    {
        if (
            type
                is INamedTypeSymbol
                {
                    TypeKind: TypeKind.Class,
                    IsAbstract: false,
                    Arity: 0,
                    ContainingType: null,
                } named
            && named.SpecialType == SpecialType.None
            && named.ContainingNamespace.ToDisplayString() != "System"
            && !named
                .ContainingNamespace.ToDisplayString()
                .StartsWith("System.", StringComparison.Ordinal)
            && !IsConfiglueModel(named, cancellationToken)
            && named.InstanceConstructors.Any(static constructor =>
                constructor.DeclaredAccessibility == Accessibility.Public
                && constructor.Parameters.Length == 0
            )
        )
        {
            var members = GetMembers(named, cancellationToken).ToArray();
            if (members.Length == 0 || HasUnsupportedPocoMembers(named, cancellationToken))
            {
                pocoType = null!;
                return false;
            }

            pocoType = named;
            return true;
        }

        pocoType = null!;
        return false;
    }

    private static bool HasUnsupportedPocoMembers(
        INamedTypeSymbol pocoType,
        CancellationToken cancellationToken
    )
    {
        var hierarchy = new Stack<INamedTypeSymbol>();
        for (
            var current = pocoType;
            current is not null && current.SpecialType != SpecialType.System_Object;
            current = current.BaseType
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            hierarchy.Push(current);
        }

        while (hierarchy.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = hierarchy.Pop();
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (property.IsStatic || property.IsIndexer)
                {
                    continue;
                }

                var hasPublicGetter =
                    property.GetMethod?.DeclaredAccessibility == Accessibility.Public;
                var hasPublicSetter =
                    property.SetMethod?.DeclaredAccessibility == Accessibility.Public;
                if (!hasPublicGetter && !hasPublicSetter)
                {
                    continue;
                }

                if (
                    !hasPublicGetter
                    || !hasPublicSetter
                    || property.IsRequired
                    || property.SetMethod?.IsInitOnly == true
                )
                {
                    return true;
                }
            }

            if (
                current
                    .GetMembers()
                    .OfType<IFieldSymbol>()
                    .Any(static field =>
                        !field.IsStatic
                        && !field.IsConst
                        && field.DeclaredAccessibility == Accessibility.Public
                    )
            )
            {
                return true;
            }
        }

        return false;
    }

    private static ImmutableArray<INamedTypeSymbol> GetPocoCloneTypes(
        ImmutableArray<SymbolMemberModel> members,
        CancellationToken cancellationToken
    )
    {
        var result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<ITypeSymbol>();
        foreach (var type in members.Select(static member => member.Property.Type))
        {
            pending.Push(type);
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = pending.Pop();
            var collection = GetCollectionInfo(type);
            if (collection.CloneKind != CloneCollectionKind.Unsupported)
            {
                if (collection.ElementType is not null)
                {
                    pending.Push(collection.ElementType);
                }
                if (collection.ValueType is not null)
                {
                    pending.Push(collection.ValueType);
                }
                continue;
            }

            if (!TryGetPocoCloneType(type, cancellationToken, out var poco) || !seen.Add(poco))
            {
                continue;
            }

            result.Add(poco);
            foreach (
                var nestedType in GetMembers(poco, cancellationToken)
                    .Select(static member => member.Property.Type)
            )
            {
                pending.Push(nestedType);
            }
        }

        return result.ToImmutable();
    }

    private static bool HasConfiglueModelAttribute(
        INamedTypeSymbol model,
        CancellationToken cancellationToken
    )
    {
        foreach (var attribute in model.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass?.ToDisplayString() == ModelAttributeName)
            {
                return true;
            }
        }

        return false;
    }
}
