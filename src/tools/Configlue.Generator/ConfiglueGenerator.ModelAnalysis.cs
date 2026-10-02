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
        if (
            type
            is not INamedTypeSymbol
            {
                TypeKind: TypeKind.Class or TypeKind.Struct,
                IsAbstract: false,
            } named
        )
        {
            return false;
        }

        return HasConfiglueModelAttribute(named, cancellationToken);
    }

    private enum StructuralTypeKind
    {
        RootModel,
        StructuralObject,
        Collection,
        Scalar,
    }

    private static StructuralTypeKind ClassifyStructuralType(
        ITypeSymbol type,
        CancellationToken cancellationToken
    )
    {
        if (
            type is IArrayTypeSymbol
            || (
                type is INamedTypeSymbol arrayLike
                && GetCollectionInfo(arrayLike).Kind != CollectionKind.Unsupported
            )
        )
        {
            return StructuralTypeKind.Collection;
        }

        if (
            type
                is not INamedTypeSymbol
                {
                    TypeKind: TypeKind.Class,
                    IsAbstract: false,
                    Arity: 0,
                } named
            || named.SpecialType != SpecialType.None
            || IsFrameworkType(named)
            || !HasPublicParameterlessConstructor(named, cancellationToken)
        )
        {
            return StructuralTypeKind.Scalar;
        }

        if (IsConfiglueModel(named, cancellationToken))
        {
            return StructuralTypeKind.RootModel;
        }

        if (
            HasUnsupportedPocoMembers(named, cancellationToken)
            || !HasPublicSettableMember(named, cancellationToken)
        )
        {
            return StructuralTypeKind.Scalar;
        }

        return StructuralTypeKind.StructuralObject;
    }

    private static bool IsStructuralType(ITypeSymbol type, CancellationToken cancellationToken)
    {
        return type is INamedTypeSymbol named
            && ClassifyStructuralType(type, cancellationToken)
                == StructuralTypeKind.StructuralObject
            && IsAccessibleForGeneration(named);
    }

    private static bool IsFrameworkType(INamedTypeSymbol type)
    {
        var namespaceName = type.ContainingNamespace.ToDisplayString();
        if (
            namespaceName == "System"
            || namespaceName.StartsWith("System.", StringComparison.Ordinal)
            || namespaceName == "Microsoft"
            || namespaceName.StartsWith("Microsoft.", StringComparison.Ordinal)
        )
        {
            return true;
        }

        var assemblyName = type.ContainingAssembly?.Name;
        return assemblyName is not null
            && (
                assemblyName.StartsWith("System.", StringComparison.Ordinal)
                || assemblyName.StartsWith("Microsoft.", StringComparison.Ordinal)
            );
    }

    private static bool IsAccessibleForGeneration(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAccessibleForClone(INamedTypeSymbol type)
    {
        if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
        {
            return false;
        }

        for (
            var current = type.ContainingType;
            current is not null;
            current = current.ContainingType
        )
        {
            if (
                current.DeclaredAccessibility
                is not (Accessibility.Public or Accessibility.Internal)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasPublicSettableMember(
        INamedTypeSymbol type,
        CancellationToken cancellationToken
    )
    {
        for (
            var current = type;
            current is not null && current.SpecialType != SpecialType.System_Object;
            current = current.BaseType
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    !property.IsStatic
                    && !property.IsIndexer
                    && property.GetMethod?.DeclaredAccessibility == Accessibility.Public
                    && property.SetMethod?.DeclaredAccessibility == Accessibility.Public
                )
                {
                    return true;
                }
            }

            foreach (var field in current.GetMembers().OfType<IFieldSymbol>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    !field.IsStatic
                    && !field.IsConst
                    && field.DeclaredAccessibility == Accessibility.Public
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetPocoCloneType(
        ITypeSymbol type,
        CancellationToken cancellationToken,
        out INamedTypeSymbol pocoType
    )
    {
        if (
            type is INamedTypeSymbol named
            && ClassifyStructuralType(type, cancellationToken)
                == StructuralTypeKind.StructuralObject
            && IsAccessibleForClone(named)
        )
        {
            var members = GetMembers(named, cancellationToken).ToArray();
            if (members.Length == 0)
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

    private static string StructuralHostName(
        INamedTypeSymbol type,
        CancellationToken cancellationToken
    )
    {
        var name = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            .ToDisplayString(TypeFormat);
        var assembly = type.ContainingAssembly?.Name ?? string.Empty;
        return "__ConfiglueStructural_"
            + GetStableTypeHash(assembly + "|" + name, cancellationToken);
    }

    private static bool HasUnsupportedPocoMembers(
        INamedTypeSymbol pocoType,
        CancellationToken cancellationToken
    ) =>
        SparseFragments.Generator.Shared.ModelConstructionPlan.HasUnsupportedStructuralMembers(
            pocoType,
            cancellationToken
        );

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
