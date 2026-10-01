using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using SparseFragments.Generator.Shared;

namespace SparseFragments.Generator.Shared;

internal readonly record struct SparseTypeModel(
    string Name,
    string NonNullableName,
    string RuntimeName,
    bool IsReferenceType,
    bool IsFragmentModel,
    string? PocoCloneHelperName
);

internal readonly record struct SparsePropertyModel(string Name, SparseTypeModel Type);

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

internal readonly record struct SparseMemberModel(
    int Id,
    SparsePropertyModel Property,
    SparseTypeModel? ChildModel,
    int MergeMode,
    SparseCollectionInfo Collection,
    SparseTypeModel? MergeStrategyType,
    string? ChildFragmentType,
    bool ChildIsStructural,
    bool ChildIsReferenceType
);

internal readonly record struct SparseModelInfo(
    string Name,
    string ModelTypeName,
    string Namespace,
    bool IsGlobalNamespace,
    bool IsStruct,
    bool IsRecord,
    string HintName
);

internal sealed record SparseStructuralModel(
    string HostName,
    string ValueTypeName,
    ImmutableArray<SparseMemberModel> Members
);

internal sealed record SparsePocoCloneModel(
    SparseModelInfo Model,
    string CloneHelperName,
    ImmutableArray<SparseMemberModel> Members
);

internal sealed record SparseGenerationAnalysis(
    SparseModelInfo? Model,
    ImmutableArray<SparseMemberModel> Members,
    ImmutableArray<SparsePocoCloneModel> PocoCloneModels,
    ImmutableArray<SparseStructuralModel> StructuralModels,
    ImmutableArray<SparseGeneratorDiagnostic> Diagnostics
);

internal sealed record SparseGenerationResult(
    string? HintName,
    string? Source,
    ImmutableArray<SparseGeneratorDiagnostic> Diagnostics
);

internal readonly record struct SparseGeneratorDiagnostic(
    string DescriptorId,
    Location? Location,
    string? Argument1
);
