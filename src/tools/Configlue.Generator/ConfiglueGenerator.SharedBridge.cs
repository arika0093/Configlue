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
    private static readonly SparseFragmentCoreEmitter FragmentCore = new(
        "global::Configlue.Optional",
        "__configlue_merge_strategy_",
        "__configlue_clone_context",
        "global::Configlue.CompilerServices.ConfiglueReferenceEqualityComparer",
        new SparseFragmentExpressions(
            "__configlue_clone_context",
            "global::Configlue.ConfiglueValueComparer",
            "global::Configlue.ConfiglueCollectionMerger"
        )
    );

    private static SparseTypeModel ToSparseType(TypeModel type) =>
        new(
            type.Name,
            type.NonNullableName,
            type.RuntimeName,
            type.IsReferenceType,
            type.IsConfiglueType,
            type.PocoCloneHelperName
        );

    private static SparseMemberModel ToSparseMember(MemberModel member) =>
        new(
            member.Id,
            new SparsePropertyModel(
                member.Property.Name,
                ToSparseType(member.Property.Type),
                member.Property.IsInitOnly,
                member.Property.IsRequired,
                member.Property.IsReadOnly
            ),
            member.ChildModel is { } child ? ToSparseType(child) : null,
            member.MergeMode,
            new SparseCollectionInfo(
                member.Collection.Kind,
                member.Collection.CloneKind,
                ToSparseType(member.Collection.ElementType),
                member.Collection.ValueType is { } value ? ToSparseType(value) : null,
                member.Collection.NamedTypeDefinition
            ),
            member.MergeStrategyType is { } strategy ? ToSparseType(strategy) : null,
            member.ChildFragmentType,
            member.ChildIsStructural,
            member.ChildIsReferenceType
        );

    private static readonly SparseGeneratorConfig SparseConfiguration = new(
        ModelAttributeName,
        MergeAttributeName,
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
