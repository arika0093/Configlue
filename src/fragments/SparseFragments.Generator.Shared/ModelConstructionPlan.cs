using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace SparseFragments.Generator.Shared;

/// <summary>Shared capability plan for constructing and projecting model values.</summary>
internal readonly record struct ModelConstructionPlan(bool CanOverlayAfterConstruction)
{
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
