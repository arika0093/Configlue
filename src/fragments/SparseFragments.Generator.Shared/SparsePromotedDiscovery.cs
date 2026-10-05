using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparseFragments.Generator.Shared;

/// <summary>Discovers reachable partial POCOs that receive first-class Fragment/Patch APIs.</summary>
/// <remarks>
/// Reachable + partial types expose <c>T.Fragment</c> / <c>T.FragmentBuilder</c> / <c>T.Patch</c>
/// without becoming independent roots. Reachable + non-partial types keep participating
/// structurally through the existing <c>__SparseStructural_*</c> hosts.
/// </remarks>
internal static class SparsePromotedDiscovery
{
    internal static bool IsPartialType(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                reference.GetSyntax(cancellationToken)
                is TypeDeclarationSyntax declaration
                && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)
            )
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsPromotablePartial(
        INamedTypeSymbol type,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (type.ContainingType is not null || type.Arity != 0 || type.IsAbstract)
        {
            return false;
        }

        if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
        {
            return false;
        }

        if (type.IsRefLikeType)
        {
            return false;
        }

        if (SparseModelDiscovery.IsFragmentModel(type, config, cancellationToken))
        {
            return false;
        }

        if (!IsPartialType(type, cancellationToken))
        {
            return false;
        }

        if (!SparseModelDiscovery.IsAccessibleForGeneration(type))
        {
            return false;
        }

        if (type.SpecialType != SpecialType.None || SparseModelDiscovery.IsFrameworkType(type))
        {
            return false;
        }

        if (
            ModelConstructorBinding.AnalyzeStructural(type, cancellationToken) is null
            && ModelConstructorBinding.AnalyzeRoot(type, cancellationToken) is null
        )
        {
            return false;
        }

        if (ModelConstructionPlan.HasUnsupportedStructuralMembers(type, cancellationToken))
        {
            return false;
        }

        if (!SparseModelDiscovery.GetReadableProperties(type, cancellationToken).Any())
        {
            return false;
        }

        return true;
    }

    internal static ImmutableArray<INamedTypeSymbol> CollectPromotedTypes(
        ImmutableArray<SparseSymbolMemberModel> members,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        var result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<ITypeSymbol>();
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pending.Push(member.Property.Type);
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = pending.Pop();
            foreach (var promotable in UnwrapPromotable(type, config, cancellationToken))
            {
                if (!seen.Add(promotable))
                {
                    continue;
                }

                result.Add(promotable);
                foreach (
                    var nested in SparseModelDiscovery
                        .GetMembers(promotable, config, cancellationToken)
                        .Select(static member => member.Property.Type)
                )
                {
                    pending.Push(nested);
                }
            }

            foreach (var nested in UnwrapCollectionElementTypes(type, cancellationToken))
            {
                pending.Push(nested);
            }
        }

        var ordered = result
            .ToImmutable()
            .Sort(static (left, right) =>
            {
                var leftName = left.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var rightName = right.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                return string.Compare(leftName, rightName, System.StringComparison.Ordinal);
            });
        return ordered;
    }

    private static IEnumerable<INamedTypeSymbol> UnwrapPromotable(
        ITypeSymbol type,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        if (type is not INamedTypeSymbol named)
        {
            yield break;
        }

        var collection = SparseCollectionAnalyzer.GetCollectionInfo(named);
        if (collection.CloneKind != SparseCloneCollectionKind.Unsupported)
        {
            yield break;
        }

        if (
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1
            && named.TypeArguments[0] is INamedTypeSymbol underlying
        )
        {
            if (IsPromotablePartial(underlying, config, cancellationToken))
            {
                yield return underlying;
            }

            yield break;
        }

        if (IsPromotablePartial(named, config, cancellationToken))
        {
            yield return named;
        }
    }

    private static IEnumerable<ITypeSymbol> UnwrapCollectionElementTypes(
        ITypeSymbol type,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (type is IArrayTypeSymbol array)
        {
            yield return array.ElementType;
            yield break;
        }

        if (type is INamedTypeSymbol named)
        {
            if (
                named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                && named.TypeArguments.Length == 1
            )
            {
                yield return named.TypeArguments[0];
                yield break;
            }

            var collection = SparseCollectionAnalyzer.GetCollectionInfo(named);
            if (collection.CloneKind != SparseCloneCollectionKind.Unsupported)
            {
                if (collection.ElementType is not null)
                {
                    yield return collection.ElementType;
                }

                if (collection.ValueType is not null)
                {
                    yield return collection.ValueType;
                }
            }
        }
    }
}
