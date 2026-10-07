using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
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

    private static bool TryGetPocoCloneType(
        ITypeSymbol type,
        CancellationToken cancellationToken,
        out INamedTypeSymbol pocoType
    )
    {
        if (
            type is INamedTypeSymbol promotable
            && SparseFragments.Generator.Shared.SparsePromotedDiscovery.IsPromotablePartial(
                promotable,
                SparseConfiguration,
                cancellationToken
            )
        )
        {
            pocoType = null!;
            return false;
        }

        if (
            type is INamedTypeSymbol named
            && SparseFragments.Generator.Shared.SparseModelDiscovery.ClassifyStructuralType(
                type,
                SparseConfiguration,
                cancellationToken
            )
                == SparseFragments
                    .Generator
                    .Shared
                    .SparseModelDiscovery
                    .StructuralTypeKind
                    .StructuralObject
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
        var name = SparseFragments.Generator.Shared.SparseNaming.NonNullableTypeName(type);
        var assembly = type.ContainingAssembly?.Name ?? string.Empty;
        return "__ConfiglueStructural_"
            + SparseFragments.Generator.Shared.SparseNaming.GetStableTypeHash(
                assembly + "|" + name,
                cancellationToken
            );
    }

    private static ImmutableArray<INamedTypeSymbol> CollectStructuralTypes(
        ImmutableArray<SparseFragments.Generator.Shared.SparseSymbolMemberModel> members,
        CancellationToken cancellationToken
    ) =>
        SparseFragments.Generator.Shared.SparseModelDiscovery.CollectStructuralTypes(
            members,
            SparseConfiguration,
            cancellationToken
        );

    private static StructuralModel CreateStructuralModel(
        INamedTypeSymbol type,
        CancellationToken cancellationToken
    )
    {
        return new StructuralModel(
            StructuralHostName(type, cancellationToken),
            NonNullableTypeName(type),
            CreateMemberModels(
                GetMembers(type, cancellationToken).ToImmutableArray(),
                cancellationToken
            ),
            SparseFragments.Generator.Shared.ModelConstructorBinding.AnalyzeStructural(
                type,
                cancellationToken
            )
        );
    }
}
