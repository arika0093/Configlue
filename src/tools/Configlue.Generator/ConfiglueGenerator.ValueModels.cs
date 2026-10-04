using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    private readonly record struct TypeModel
    {
        public TypeModel(
            string name,
            string nonNullableName,
            string runtimeName,
            bool isReferenceType,
            bool isConfiglueModel,
            string? pocoCloneHelperName
        )
        {
            Name = name;
            NonNullableName = nonNullableName;
            RuntimeName = runtimeName;
            IsReferenceType = isReferenceType;
            IsConfiglueType = isConfiglueModel;
            PocoCloneHelperName = pocoCloneHelperName;
        }

        public string Name { get; init; }
        public string NonNullableName { get; init; }
        public string RuntimeName { get; init; }
        public bool IsReferenceType { get; init; }
        public bool IsConfiglueType { get; init; }
        public string? PocoCloneHelperName { get; init; }
    }

    private readonly record struct PropertyModel
    {
        public PropertyModel(
            string name,
            TypeModel type,
            bool isRequired,
            string? jsonPropertyName,
            bool hasExplicitJsonPropertyName,
            string? environmentVariableName,
            bool isInitOnly = false,
            bool isReadOnly = false,
            int jsonIgnoreCondition = 0
        )
        {
            Name = name;
            Type = type;
            IsRequired = isRequired;
            JsonPropertyName = jsonPropertyName;
            HasExplicitJsonPropertyName = hasExplicitJsonPropertyName;
            EnvironmentVariableName = environmentVariableName;
            IsInitOnly = isInitOnly;
            IsReadOnly = isReadOnly;
            JsonIgnoreCondition = jsonIgnoreCondition;
        }

        public string Name { get; init; }
        public TypeModel Type { get; init; }
        public bool IsRequired { get; init; }
        public bool IsInitOnly { get; init; }
        public bool IsReadOnly { get; init; }
        public string? JsonPropertyName { get; init; }
        public bool HasExplicitJsonPropertyName { get; init; }
        public string? EnvironmentVariableName { get; init; }
        public int JsonIgnoreCondition { get; init; }

        public bool IsJsonIgnored => JsonIgnoreCondition == 1;

        public bool IsJsonIgnoreWhenWritingNull => JsonIgnoreCondition == 3;

        public bool IsJsonIgnoreWhenWritingDefault => JsonIgnoreCondition == 2;
    }

    private readonly record struct CollectionInfo
    {
        public CollectionInfo(
            CollectionKind kind,
            CloneCollectionKind cloneKind,
            TypeModel elementType,
            TypeModel? valueType,
            string? namedTypeDefinition
        )
        {
            Kind = kind;
            CloneKind = cloneKind;
            ElementType = elementType;
            ValueType = valueType;
            NamedTypeDefinition = namedTypeDefinition;
        }

        public CollectionKind Kind { get; init; }
        public CloneCollectionKind CloneKind { get; init; }
        public TypeModel ElementType { get; init; }
        public TypeModel? ValueType { get; init; }
        public string? NamedTypeDefinition { get; init; }
        public static CollectionInfo Unsupported { get; } =
            new(CollectionKind.Unsupported, CloneCollectionKind.Unsupported, default, null, null);
    }

    private readonly record struct MemberModel
    {
        public MemberModel(
            int id,
            PropertyModel property,
            TypeModel? childModel,
            int mergeMode,
            CollectionInfo collection,
            TypeModel? mergeStrategyType,
            string? childFragmentType = null,
            string? childPatchType = null,
            string? childSchemaType = null,
            string? childDetailsType = null,
            bool childIsStructural = false,
            bool childIsReferenceType = true
        )
        {
            Id = id;
            Property = property;
            ChildModel = childModel;
            MergeMode = mergeMode;
            Collection = collection;
            MergeStrategyType = mergeStrategyType;
            ChildFragmentType = childFragmentType;
            ChildPatchType = childPatchType;
            ChildSchemaType = childSchemaType;
            ChildDetailsType = childDetailsType;
            ChildIsStructural = childIsStructural;
            ChildIsReferenceType = childIsReferenceType;
        }

        public int Id { get; init; }
        public PropertyModel Property { get; init; }
        public TypeModel? ChildModel { get; init; }
        public int MergeMode { get; init; }
        public CollectionInfo Collection { get; init; }
        public TypeModel? MergeStrategyType { get; init; }
        public string? ChildFragmentType { get; init; }
        public string? ChildPatchType { get; init; }
        public string? ChildSchemaType { get; init; }
        public string? ChildDetailsType { get; init; }
        public bool ChildIsStructural { get; init; }
        public bool ChildIsReferenceType { get; init; }
    }

    private readonly record struct ModelInfo
    {
        public ModelInfo(
            string name,
            string modelTypeName,
            string fullyQualifiedName,
            string @namespace,
            bool isGlobalNamespace,
            bool isStruct,
            bool isReadOnly,
            bool isRecord,
            bool isPublic,
            string modelId,
            int version,
            SparseFragments.Generator.Shared.ModelConstructorBinding? constructor
        )
        {
            Name = name;
            ModelTypeName = modelTypeName;
            FullyQualifiedName = fullyQualifiedName;
            Namespace = @namespace;
            IsGlobalNamespace = isGlobalNamespace;
            IsStruct = isStruct;
            IsReadOnly = isReadOnly;
            IsRecord = isRecord;
            IsPublic = isPublic;
            ModelId = modelId;
            Version = version;
            Constructor = constructor;
        }

        public string Name { get; init; }
        public string ModelTypeName { get; init; }
        public string FullyQualifiedName { get; init; }
        public string Namespace { get; init; }
        public bool IsGlobalNamespace { get; init; }
        public bool IsStruct { get; init; }
        public bool IsReadOnly { get; init; }
        public bool IsRecord { get; init; }
        public bool IsPublic { get; init; }
        public string ModelId { get; init; }
        public int Version { get; init; }
        public SparseFragments.Generator.Shared.ModelConstructorBinding? Constructor { get; init; }
    }

    private readonly record struct ModelIdentity(
        string Name,
        string Id,
        int Version,
        GeneratorLocationInfo Location
    );

    private readonly record struct PreviousMemberMapping
    {
        public PreviousMemberMapping(
            MemberModel currentMember,
            MemberModel previousMember,
            bool hasSameType,
            bool canMigrateChild
        )
        {
            CurrentMember = currentMember;
            PreviousMember = previousMember;
            HasSameType = hasSameType;
            CanMigrateChild = canMigrateChild;
        }

        public MemberModel CurrentMember { get; init; }
        public MemberModel PreviousMember { get; init; }
        public bool HasSameType { get; init; }
        public bool CanMigrateChild { get; init; }
    }

    private sealed class PreviousModelInfo : IEquatable<PreviousModelInfo>
    {
        public PreviousModelInfo(ModelInfo model, ImmutableArray<PreviousMemberMapping> mappings)
        {
            Model = model;
            Mappings = mappings;
        }

        public ModelInfo Model { get; }
        public ImmutableArray<PreviousMemberMapping> Mappings { get; }

        public bool Equals(PreviousModelInfo? other)
        {
            return ReferenceEquals(this, other)
                || (
                    other is not null
                    && Model.Equals(other.Model)
                    && SequenceEqual(Mappings, other.Mappings)
                );
        }

        public override bool Equals(object? obj) => obj is PreviousModelInfo other && Equals(other);

        public override int GetHashCode()
        {
            var hash = Model.GetHashCode();
            foreach (var mapping in Mappings)
            {
                hash = unchecked(hash * 31 + mapping.GetHashCode());
            }

            return hash;
        }
    }

    private sealed class PocoCloneModel : IEquatable<PocoCloneModel>
    {
        public PocoCloneModel(
            ModelInfo model,
            string cloneHelperName,
            ImmutableArray<MemberModel> members
        )
        {
            Model = model;
            CloneHelperName = cloneHelperName;
            Members = members;
        }

        public ModelInfo Model { get; }
        public string CloneHelperName { get; }
        public ImmutableArray<MemberModel> Members { get; }

        public bool Equals(PocoCloneModel? other)
        {
            return ReferenceEquals(this, other)
                || (
                    other is not null
                    && Model.Equals(other.Model)
                    && string.Equals(
                        CloneHelperName,
                        other.CloneHelperName,
                        StringComparison.Ordinal
                    )
                    && SequenceEqual(Members, other.Members)
                );
        }

        public override bool Equals(object? obj) => obj is PocoCloneModel other && Equals(other);

        public override int GetHashCode()
        {
            var hash = unchecked(
                Model.GetHashCode() * 31 + StringComparer.Ordinal.GetHashCode(CloneHelperName)
            );
            foreach (var member in Members)
            {
                hash = unchecked(hash * 31 + member.GetHashCode());
            }

            return hash;
        }
    }

    private sealed class StructuralModel : IEquatable<StructuralModel>
    {
        public StructuralModel(
            string hostName,
            string valueTypeName,
            ImmutableArray<MemberModel> members,
            SparseFragments.Generator.Shared.ModelConstructorBinding? constructor
        )
        {
            HostName = hostName;
            ValueTypeName = valueTypeName;
            Members = members;
            Constructor = constructor;
        }

        public SparseFragments.Generator.Shared.ModelConstructorBinding? Constructor { get; }
        public string HostName { get; }
        public string ValueTypeName { get; }
        public ImmutableArray<MemberModel> Members { get; }

        public bool Equals(StructuralModel? other)
        {
            return ReferenceEquals(this, other)
                || (
                    other is not null
                    && string.Equals(HostName, other.HostName, StringComparison.Ordinal)
                    && string.Equals(ValueTypeName, other.ValueTypeName, StringComparison.Ordinal)
                    && Equals(Constructor, other.Constructor)
                    && SequenceEqual(Members, other.Members)
                );
        }

        public override bool Equals(object? obj) => obj is StructuralModel other && Equals(other);

        public override int GetHashCode()
        {
            var hash = unchecked(
                StringComparer.Ordinal.GetHashCode(HostName) * 31
                + StringComparer.Ordinal.GetHashCode(ValueTypeName)
            );
            foreach (var member in Members)
            {
                hash = unchecked(hash * 31 + member.GetHashCode());
            }

            return unchecked(hash * 31 + (Constructor?.GetHashCode() ?? 0));
        }
    }

    private sealed class GenerationAnalysis : IEquatable<GenerationAnalysis>
    {
        public GenerationAnalysis(
            string? hintName,
            ModelInfo? model,
            ImmutableArray<MemberModel> members,
            ImmutableArray<PreviousModelInfo> previousModels,
            ImmutableArray<PocoCloneModel> pocoCloneModels,
            ImmutableArray<StructuralModel> structuralModels,
            ImmutableArray<GeneratorDiagnosticInfo> diagnostics
        )
        {
            HintName = hintName;
            Model = model;
            Members = members;
            PreviousModels = previousModels;
            PocoCloneModels = pocoCloneModels;
            StructuralModels = structuralModels;
            Diagnostics = diagnostics;
        }

        public string? HintName { get; }
        public ModelInfo? Model { get; }
        public ImmutableArray<MemberModel> Members { get; }
        public ImmutableArray<PreviousModelInfo> PreviousModels { get; }
        public ImmutableArray<PocoCloneModel> PocoCloneModels { get; }
        public ImmutableArray<StructuralModel> StructuralModels { get; }
        public ImmutableArray<GeneratorDiagnosticInfo> Diagnostics { get; }

        public bool Equals(GenerationAnalysis? other)
        {
            return ReferenceEquals(this, other)
                || (
                    other is not null
                    && string.Equals(HintName, other.HintName, StringComparison.Ordinal)
                    && Nullable.Equals(Model, other.Model)
                    && SequenceEqual(Members, other.Members)
                    && SequenceEqual(PreviousModels, other.PreviousModels)
                    && SequenceEqual(PocoCloneModels, other.PocoCloneModels)
                    && SequenceEqual(StructuralModels, other.StructuralModels)
                    && SequenceEqual(Diagnostics, other.Diagnostics)
                );
        }

        public override bool Equals(object? obj) =>
            obj is GenerationAnalysis other && Equals(other);

        public override int GetHashCode()
        {
            var hash = unchecked(
                (HintName is null ? 0 : StringComparer.Ordinal.GetHashCode(HintName)) * 31
                + (Model?.GetHashCode() ?? 0)
            );
            foreach (var member in Members)
            {
                hash = unchecked(hash * 31 + member.GetHashCode());
            }
            foreach (var previousModel in PreviousModels)
            {
                hash = unchecked(hash * 31 + previousModel.GetHashCode());
            }
            foreach (var pocoModel in PocoCloneModels)
            {
                hash = unchecked(hash * 31 + pocoModel.GetHashCode());
            }
            foreach (var structuralModel in StructuralModels)
            {
                hash = unchecked(hash * 31 + structuralModel.GetHashCode());
            }
            foreach (var diagnostic in Diagnostics)
            {
                hash = unchecked(hash * 31 + diagnostic.GetHashCode());
            }

            return hash;
        }
    }

    private static TypeModel CreateTypeModel(ITypeSymbol type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isConfiglueModel = IsConfiglueModel(type, cancellationToken);
        string? pocoCloneHelperName = null;
        if (!isConfiglueModel && TryGetPocoCloneType(type, cancellationToken, out var pocoType))
        {
            var cloneTypeName = pocoType
                .WithNullableAnnotation(NullableAnnotation.NotAnnotated)
                .ToDisplayString();
            pocoCloneHelperName = "__Clone_" + GetStableTypeHash(cloneTypeName, cancellationToken);
        }

        return new TypeModel(
            TypeName(type),
            NonNullableTypeName(type),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.IsReferenceType,
            isConfiglueModel,
            pocoCloneHelperName
        );
    }

    private static MemberModel CreateMemberModel(
        SymbolMemberModel member,
        CancellationToken cancellationToken
    )
    {
        var jsonPropertyName = GetJsonPropertyName(
            member.Property,
            cancellationToken,
            out var hasExplicitJsonPropertyName
        );
        var property = new PropertyModel(
            member.Property.Name,
            CreateTypeModel(member.Property.Type, cancellationToken),
            SparseFragments.Generator.Shared.RoslynSymbolCompat.IsRequired(member.Property),
            jsonPropertyName,
            hasExplicitJsonPropertyName,
            GetEnvironmentVariableName(member.Property, cancellationToken),
            member.Property.SetMethod?.IsInitOnly == true,
            member.Property.SetMethod is null,
            GetJsonIgnoreCondition(member.Property, cancellationToken)
        );
        TypeModel? childModel = null;
        string? childFragmentType = null;
        string? childPatchType = null;
        string? childSchemaType = null;
        string? childDetailsType = null;
        var childIsStructural = false;
        var childIsReferenceType = true;
        if (member.ChildModel is not null)
        {
            childModel = CreateTypeModel(member.ChildModel, cancellationToken);
            childIsReferenceType = member.ChildModel.IsReferenceType;
            if (IsConfiglueModel(member.ChildModel, cancellationToken))
            {
                var childType = NonNullableTypeName(member.ChildModel);
                childFragmentType = childType + ".Fragment";
                childPatchType = childType + ".Patch";
                childSchemaType = childType;
                childDetailsType = childType + ".Details";
            }
            else
            {
                var host = StructuralHostName(member.ChildModel, cancellationToken);
                childIsStructural = true;
                childFragmentType = host + ".Fragment";
                childPatchType = host + ".Patch";
                childSchemaType = host;
                childDetailsType = host + ".Details";
            }
        }

        TypeModel? mergeStrategyType = null;
        if (member.MergeStrategyType is not null)
        {
            mergeStrategyType = CreateTypeModel(member.MergeStrategyType, cancellationToken);
        }
        var collection = CreateCollectionInfo(member.Collection, cancellationToken);
        return new MemberModel(
            member.Id,
            property,
            childModel,
            member.MergeMode,
            collection,
            mergeStrategyType,
            childFragmentType,
            childPatchType,
            childSchemaType,
            childDetailsType,
            childIsStructural,
            childIsReferenceType
        );
    }

    private static CollectionInfo CreateCollectionInfo(
        SymbolCollectionInfo collection,
        CancellationToken cancellationToken
    )
    {
        if (
            collection.Kind == CollectionKind.Unsupported
            && collection.CloneKind == CloneCollectionKind.Unsupported
        )
        {
            return CollectionInfo.Unsupported;
        }

        TypeModel? valueType = null;
        if (collection.ValueType is not null)
        {
            valueType = CreateTypeModel(collection.ValueType, cancellationToken);
        }

        return new CollectionInfo(
            collection.Kind,
            collection.CloneKind,
            CreateTypeModel(collection.ElementType, cancellationToken),
            valueType,
            collection.NamedType?.ConstructedFrom.ToDisplayString()
        );
    }

    private static ImmutableArray<MemberModel> CreateMemberModels(
        ImmutableArray<SymbolMemberModel> members,
        CancellationToken cancellationToken
    )
    {
        var result = ImmutableArray.CreateBuilder<MemberModel>(members.Length);
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(CreateMemberModel(member, cancellationToken));
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<PreviousModelInfo> CreatePreviousModelInfos(
        ImmutableArray<SymbolPreviousModelInfo> previousModels,
        ImmutableArray<SymbolMemberModel> currentMembers,
        ImmutableArray<MemberModel> currentValueMembers,
        CancellationToken cancellationToken
    )
    {
        var result = ImmutableArray.CreateBuilder<PreviousModelInfo>(previousModels.Length);

        foreach (var previousModel in previousModels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previousMembers = CreateMemberModels(previousModel.Members, cancellationToken);
            var previousMembersByName = new Dictionary<string, MemberModel>(
                previousMembers.Length,
                StringComparer.Ordinal
            );
            var previousSymbolMembersByName = new Dictionary<string, SymbolMemberModel>(
                previousModel.Members.Length,
                StringComparer.Ordinal
            );
            for (var index = 0; index < previousModel.Members.Length; index++)
            {
                var previousSymbolMember = previousModel.Members[index];
                previousSymbolMembersByName.Add(
                    previousSymbolMember.Property.Name,
                    previousSymbolMember
                );
                previousMembersByName.Add(
                    previousSymbolMember.Property.Name,
                    previousMembers[index]
                );
            }

            var mappings = ImmutableArray.CreateBuilder<PreviousMemberMapping>();
            for (var index = 0; index < currentMembers.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentMember = currentMembers[index];
                var name = currentMember.Property.Name;
                if (
                    !previousMembersByName.TryGetValue(name, out var previousValueMember)
                    || !previousSymbolMembersByName.TryGetValue(name, out var previousSymbolMember)
                )
                {
                    continue;
                }

                var currentValueMember = currentValueMembers[index];
                var hasSameType = SymbolEqualityComparer.Default.Equals(
                    previousSymbolMember.Property.Type,
                    currentMember.Property.Type
                );
                var canMigrateChild =
                    !hasSameType
                    && currentMember.ChildModel is not null
                    && previousSymbolMember.ChildModel is not null
                    && HasPreviousVersion(
                        currentMember.ChildModel,
                        previousSymbolMember.ChildModel,
                        cancellationToken
                    );
                mappings.Add(
                    new PreviousMemberMapping(
                        currentValueMember,
                        previousValueMember,
                        hasSameType,
                        canMigrateChild
                    )
                );
            }

            var previousModelId = GetModelId(previousModel.Model, cancellationToken);
            var previousModelVersion = GetModelVersion(previousModel.Model, cancellationToken);
            result.Add(
                new PreviousModelInfo(
                    CreateModelInfo(
                        previousModel.Model,
                        previousModelId,
                        previousModelVersion,
                        cancellationToken
                    ),
                    mappings.ToImmutable()
                )
            );
        }

        return result.ToImmutable();
    }

    private static PocoCloneModel CreatePocoCloneModel(
        INamedTypeSymbol pocoType,
        CancellationToken cancellationToken
    )
    {
        var typeName = pocoType
            .WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            .ToDisplayString();
        return new PocoCloneModel(
            CreateModelInfo(pocoType, string.Empty, InitialSchemaVersion, cancellationToken) with
            {
                Constructor =
                    SparseFragments.Generator.Shared.ModelConstructorBinding.AnalyzeStructural(
                        pocoType,
                        cancellationToken
                    ),
            },
            "__Clone_" + GetStableTypeHash(typeName, cancellationToken),
            CreateMemberModels(
                GetMembers(pocoType, cancellationToken).ToImmutableArray(),
                cancellationToken
            )
        );
    }

    private static ModelInfo CreateModelInfo(
        INamedTypeSymbol model,
        string modelId,
        int version,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ModelInfo(
            model.Name,
            NonNullableTypeName(model),
            model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            model.ContainingNamespace.ToDisplayString(),
            model.ContainingNamespace.IsGlobalNamespace,
            model.TypeKind == TypeKind.Struct,
            model.IsReadOnly,
            model.IsRecord,
            model.DeclaredAccessibility == Accessibility.Public,
            modelId,
            version,
            SparseFragments.Generator.Shared.ModelConstructorBinding.AnalyzeRoot(
                model,
                cancellationToken
            )
        );
    }

    private static bool SequenceEqual<T>(ImmutableArray<T> left, ImmutableArray<T> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var comparer = EqualityComparer<T>.Default;
        for (var index = 0; index < left.Length; index++)
        {
            if (!comparer.Equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }
}
