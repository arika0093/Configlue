using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace SparseFragments.Generator.Shared;

/// <summary>Shared capability plan for constructing and projecting model values.</summary>
internal readonly record struct ModelConstructionPlan(bool CanOverlayAfterConstruction)
{
    public static bool HasUnsupportedStructuralMembers(
        INamedTypeSymbol pocoType,
        CancellationToken cancellationToken
    )
    {
        var constructor = ModelConstructorBinding.AnalyzeStructural(pocoType, cancellationToken);
        if (constructor is null)
            return true;
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
                    || (
                        !hasPublicSetter
                        && !(
                            property.SetMethod is null
                            && constructor.Parameters.Any(parameter =>
                                parameter.PropertyName == property.Name
                            )
                        )
                    )
                    || (RoslynSymbolCompat.IsRequired(property) && !constructor.SetsRequiredMembers)
                    || (
                        property.SetMethod?.IsInitOnly == true
                        && !constructor.Parameters.Any(parameter =>
                            parameter.PropertyName == property.Name
                        )
                    )
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

    public static IEnumerable<ISymbol> UnsupportedRequiredMembers(
        INamedTypeSymbol model,
        IEnumerable<IPropertySymbol> properties,
        CancellationToken cancellationToken
    )
    {
        var represented = new HashSet<ISymbol>(properties, SymbolEqualityComparer.Default);
        for (var type = model; type is not null; type = type.BaseType)
        {
            foreach (var member in type.GetMembers())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    member is IPropertySymbol or IFieldSymbol
                    && RoslynSymbolCompat.IsRequired(member)
                    && !represented.Contains(member)
                )
                    yield return member;
            }
        }
    }

    public static ModelConstructionPlan ForStructuralMembers(
        ImmutableArray<SparseMemberModel> members,
        ModelConstructorBinding? constructor
    ) =>
        new(
            members.All(member =>
                (!member.Property.IsRequired || constructor?.SetsRequiredMembers == true)
                && (
                    !member.Property.IsInitOnly
                    || (
                        constructor is not null
                        && constructor.Parameters.Any(parameter =>
                            parameter.PropertyName == member.Property.Name
                        )
                    )
                )
            )
        );

    public static ModelConstructionPlan ForMembers(ImmutableArray<SparseMemberModel> members) =>
        new(
            members.All(static member => !member.Property.IsInitOnly && !member.Property.IsRequired)
        );

    public static bool HasRootParameterlessConstructor(
        INamedTypeSymbol model,
        CancellationToken cancellationToken
    )
    {
        foreach (var constructor in model.InstanceConstructors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (constructor.Parameters.IsEmpty)
                return true;
        }
        return model.IsValueType;
    }
}
