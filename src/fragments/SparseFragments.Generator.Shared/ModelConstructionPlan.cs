using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace SparseFragments.Generator.Shared;

/// <summary>Shared capability plan for constructing and projecting model values.</summary>
internal readonly record struct ModelConstructionPlan(bool CanOverlayAfterConstruction)
{
    public static ModelConstructionPlan ForMembers(ImmutableArray<SparseMemberModel> members) =>
        new(members.All(static member => !member.Property.IsInitOnly));

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
