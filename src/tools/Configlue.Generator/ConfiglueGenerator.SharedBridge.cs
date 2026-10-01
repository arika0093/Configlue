global using CloneCollectionKind = SparseFragments.Generator.Shared.SparseCloneCollectionKind;
global using CollectionKind = SparseFragments.Generator.Shared.SparseCollectionKind;
global using SymbolCollectionInfo = SparseFragments.Generator.Shared.SparseSymbolCollectionInfo;
using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using SparseFragments.Generator.Shared;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    private static readonly SparseGeneratorConfig SparseConfiguration = new(
        ModelAttributeName,
        "SparseFragments.SparseMergeAttribute",
        "Configlue.ConfiglueMergeStrategy<T>"
    );

    private static SymbolCollectionInfo GetCollectionInfo(ITypeSymbol type) =>
        SparseCollectionAnalyzer.GetCollectionInfo(type);

    private static IEnumerable<SymbolMemberModel> GetSharedSparseMembers(
        INamedTypeSymbol model,
        CancellationToken cancellationToken
    )
    {
        foreach (
            var member in SparseModelAnalyzer.GetMembers(
                model,
                SparseConfiguration,
                cancellationToken
            )
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new SymbolMemberModel(
                member.Id,
                member.Property,
                member.ChildModel,
                member.MergeMode,
                member.Collection,
                member.MergeStrategyType
            );
        }
    }
}
