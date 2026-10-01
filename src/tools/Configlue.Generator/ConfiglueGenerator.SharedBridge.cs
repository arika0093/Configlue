global using CloneCollectionKind = SparseFragments.Generator.Shared.SparseCloneCollectionKind;
global using CollectionKind = SparseFragments.Generator.Shared.SparseCollectionKind;
global using SymbolCollectionInfo = SparseFragments.Generator.Shared.SparseSymbolCollectionInfo;
using Microsoft.CodeAnalysis;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    private static SymbolCollectionInfo GetCollectionInfo(ITypeSymbol type) =>
        SparseFragments.Generator.Shared.SparseCollectionAnalyzer.GetCollectionInfo(type);
}
