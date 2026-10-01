global using CloneCollectionKind = SparseFragments.Generator.Shared.SparseCloneCollectionKind;
global using CollectionKind = SparseFragments.Generator.Shared.SparseCollectionKind;
global using SymbolCollectionInfo = SparseFragments.Generator.Shared.SparseSymbolCollectionInfo;
using System.Collections.Generic;
using System.Collections.Immutable;
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

    private static SparseModelInfo ToSparseModelInfo(ModelInfo model, string hintName) =>
        new(
            model.Name,
            model.ModelTypeName,
            model.Namespace,
            model.IsGlobalNamespace,
            model.IsStruct,
            model.IsRecord,
            hintName
        );

    private static SparseTypeModel ToSparseTypeModel(TypeModel type) =>
        new(
            type.Name,
            type.NonNullableName,
            type.RuntimeName,
            type.IsReferenceType,
            type.IsConfiglueType,
            type.PocoCloneHelperName
        );

    private static SparseCollectionInfo ToSparseCollectionInfo(CollectionInfo collection)
    {
        if (
            collection.Kind == CollectionKind.Unsupported
            && collection.CloneKind == CloneCollectionKind.Unsupported
        )
        {
            return SparseCollectionInfo.Unsupported;
        }

        return new SparseCollectionInfo(
            collection.Kind,
            collection.CloneKind,
            ToSparseTypeModel(collection.ElementType),
            collection.ValueType is { } valueType ? ToSparseTypeModel(valueType) : null,
            collection.NamedTypeDefinition
        );
    }

    private static SparseMemberModel ToSparseMemberModel(MemberModel member) =>
        new(
            member.Id,
            new SparsePropertyModel(
                member.Property.Name,
                ToSparseTypeModel(member.Property.Type)
            ),
            member.ChildModel is { } childModel ? ToSparseTypeModel(childModel) : null,
            member.MergeMode,
            ToSparseCollectionInfo(member.Collection),
            member.MergeStrategyType is { } mergeStrategy
                ? ToSparseTypeModel(mergeStrategy)
                : null,
            member.ChildFragmentType,
            member.ChildIsStructural,
            member.ChildIsReferenceType
        );

    private static ImmutableArray<SparseMemberModel> ToSparseMembers(
        ImmutableArray<MemberModel> members
    )
    {
        var result = ImmutableArray.CreateBuilder<SparseMemberModel>(members.Length);
        foreach (var member in members)
        {
            result.Add(ToSparseMemberModel(member));
        }

        return result.MoveToImmutable();
    }

    private static ImmutableArray<SparsePocoCloneModel> ToSparsePocoCloneModels(
        ImmutableArray<PocoCloneModel> models
    )
    {
        var result = ImmutableArray.CreateBuilder<SparsePocoCloneModel>(models.Length);
        foreach (var model in models)
        {
            result.Add(
                new SparsePocoCloneModel(
                    ToSparseModelInfo(model.Model, string.Empty),
                    model.CloneHelperName,
                    ToSparseMembers(model.Members)
                )
            );
        }

        return result.MoveToImmutable();
    }

    private static ImmutableArray<SparseStructuralModel> ToSparseStructuralModels(
        ImmutableArray<StructuralModel> models
    )
    {
        var result = ImmutableArray.CreateBuilder<SparseStructuralModel>(models.Length);
        foreach (var model in models)
        {
            result.Add(
                new SparseStructuralModel(
                    model.HostName,
                    model.ValueTypeName,
                    ToSparseMembers(model.Members)
                )
            );
        }

        return result.MoveToImmutable();
    }
}
