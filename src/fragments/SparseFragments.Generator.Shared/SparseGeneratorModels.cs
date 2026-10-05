using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using SparseFragments.Generator.Shared;

namespace SparseFragments.Generator.Shared;

internal readonly record struct SparseTypeModel(
    string Name,
    string NonNullableName,
    string RuntimeName,
    bool IsReferenceType,
    bool IsFragmentModel,
    string? PocoCloneHelperName,
    string PatchApiPrefix = ""
);

internal readonly record struct SparsePropertyModel(
    string Name,
    SparseTypeModel Type,
    bool IsInitOnly = false,
    bool IsRequired = false,
    bool IsReadOnly = false,
    string? JsonPropertyName = null,
    bool HasExplicitJsonPropertyName = false,
    int JsonIgnoreCondition = 0
)
{
    public bool IsJsonIgnored => JsonIgnoreCondition == 1;

    public bool IsJsonIgnoreWhenWritingNull => JsonIgnoreCondition == 3;

    public bool IsJsonIgnoreWhenWritingDefault => JsonIgnoreCondition == 2;
}

internal readonly record struct SparseCollectionInfo(
    SparseCollectionKind Kind,
    SparseCloneCollectionKind CloneKind,
    SparseTypeModel ElementType,
    SparseTypeModel? ValueType,
    string? NamedTypeDefinition
)
{
    public static SparseCollectionInfo Unsupported { get; } =
        new(
            SparseCollectionKind.Unsupported,
            SparseCloneCollectionKind.Unsupported,
            default,
            null,
            null
        );
}

internal sealed class SparseSymbolMemberModel(
    int id,
    IPropertySymbol property,
    INamedTypeSymbol? childModel,
    int mergeMode,
    SparseSymbolCollectionInfo collection,
    INamedTypeSymbol? mergeStrategyType
)
{
    public int Id { get; } = id;
    public IPropertySymbol Property { get; } = property;
    public INamedTypeSymbol? ChildModel { get; } = childModel;
    public int MergeMode { get; } = mergeMode;
    public SparseSymbolCollectionInfo Collection { get; } = collection;
    public INamedTypeSymbol? MergeStrategyType { get; } = mergeStrategyType;
}

internal readonly record struct SparseMemberModel(
    int Id,
    SparsePropertyModel Property,
    SparseTypeModel? ChildModel,
    int MergeMode,
    SparseCollectionInfo Collection,
    SparseTypeModel? MergeStrategyType,
    string? ChildFragmentType,
    bool ChildIsStructural,
    bool ChildIsReferenceType,
    bool PortableSetView = false
);

internal readonly record struct SparseModelInfo(
    string Name,
    string ModelTypeName,
    string Namespace,
    bool IsGlobalNamespace,
    bool IsStruct,
    bool IsRecord,
    string HintName,
    ModelConstructorBinding? Constructor
);

internal sealed record SparseStructuralModel(
    string HostName,
    string ValueTypeName,
    ImmutableArray<SparseMemberModel> Members,
    ModelConstructorBinding? Constructor
)
{
    public bool Equals(SparseStructuralModel? other) =>
        other is not null
        && HostName == other.HostName
        && ValueTypeName == other.ValueTypeName
        && Equals(Constructor, other.Constructor)
        && SparseSequence.Equal(Members, other.Members);

    public override int GetHashCode() =>
        unchecked(
            (HostName.GetHashCode() * 31 + ValueTypeName.GetHashCode()) * 31
            + SparseSequence.Hash(Members)
            + (Constructor?.GetHashCode() ?? 0)
        );
}

internal sealed record SparsePocoCloneModel(
    SparseModelInfo Model,
    string CloneHelperName,
    ImmutableArray<SparseMemberModel> Members
)
{
    public bool Equals(SparsePocoCloneModel? other) =>
        other is not null
        && Model == other.Model
        && CloneHelperName == other.CloneHelperName
        && SparseSequence.Equal(Members, other.Members);

    public override int GetHashCode() =>
        unchecked(
            (Model.GetHashCode() * 31 + CloneHelperName.GetHashCode()) * 31
            + SparseSequence.Hash(Members)
        );
}

internal sealed record SparsePromotedModel(
    SparseModelInfo Model,
    ImmutableArray<SparseMemberModel> Members,
    ImmutableArray<SparsePocoCloneModel> PocoCloneModels,
    ImmutableArray<SparseStructuralModel> StructuralModels
)
{
    public bool Equals(SparsePromotedModel? other) =>
        other is not null
        && Model.Equals(other.Model)
        && SparseSequence.Equal(Members, other.Members)
        && SparseSequence.Equal(PocoCloneModels, other.PocoCloneModels)
        && SparseSequence.Equal(StructuralModels, other.StructuralModels);

    public override int GetHashCode() =>
        unchecked(
            (
                (Model.GetHashCode() * 31 + SparseSequence.Hash(Members)) * 31
                + SparseSequence.Hash(PocoCloneModels)
            )
                * 31 + SparseSequence.Hash(StructuralModels)
        );
}

internal sealed record SparseGenerationAnalysis(
    SparseModelInfo? Model,
    ImmutableArray<SparseMemberModel> Members,
    ImmutableArray<SparsePocoCloneModel> PocoCloneModels,
    ImmutableArray<SparseStructuralModel> StructuralModels,
    ImmutableArray<SparseGeneratorDiagnostic> Diagnostics,
    ImmutableArray<SparsePromotedModel> PromotedModels = default
)
{
    public bool Equals(SparseGenerationAnalysis? other) =>
        other is not null
        && Model == other.Model
        && SparseSequence.Equal(Members, other.Members)
        && SparseSequence.Equal(PocoCloneModels, other.PocoCloneModels)
        && SparseSequence.Equal(StructuralModels, other.StructuralModels)
        && SparseSequence.Equal(Diagnostics, other.Diagnostics)
        && SparseSequence.Equal(PromotedModels, other.PromotedModels);

    public override int GetHashCode()
    {
        var hash = Model?.GetHashCode() ?? 0;
        hash = unchecked(hash * 31 + SparseSequence.Hash(Members));
        hash = unchecked(hash * 31 + SparseSequence.Hash(PocoCloneModels));
        hash = unchecked(hash * 31 + SparseSequence.Hash(StructuralModels));
        hash = unchecked(hash * 31 + SparseSequence.Hash(Diagnostics));
        return unchecked(hash * 31 + SparseSequence.Hash(PromotedModels));
    }
}

internal sealed record SparseGenerationResult(
    string? HintName,
    string? Source,
    ImmutableArray<SparseGeneratorDiagnostic> Diagnostics
)
{
    public bool Equals(SparseGenerationResult? other) =>
        other is not null
        && HintName == other.HintName
        && Source == other.Source
        && SparseSequence.Equal(Diagnostics, other.Diagnostics);

    public override int GetHashCode() =>
        unchecked(
            ((HintName?.GetHashCode() ?? 0) * 31 + (Source?.GetHashCode() ?? 0)) * 31
            + SparseSequence.Hash(Diagnostics)
        );
}

internal static class SparseSequence
{
    public static bool Equal<T>(ImmutableArray<T> left, ImmutableArray<T> right) =>
        left.IsDefault || right.IsDefault
            ? left.IsDefault == right.IsDefault
            : left.SequenceEqual(right);

    public static int Hash<T>(ImmutableArray<T> values)
    {
        if (values.IsDefault)
            return 0;
        var hash = 17;
        foreach (var value in values)
        {
            hash = unchecked(
                hash * 31 + (value is null ? 0 : EqualityComparer<T>.Default.GetHashCode(value))
            );
        }
        return hash;
    }
}

internal readonly record struct SparseGeneratorDiagnostic(
    string DescriptorId,
    Location? Location,
    string? Argument1
);
