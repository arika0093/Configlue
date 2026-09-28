using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    private readonly struct TypeModel : IEquatable<TypeModel>
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

        public string Name { get; }
        public string NonNullableName { get; }
        public string RuntimeName { get; }
        public bool IsReferenceType { get; }
        public bool IsConfiglueType { get; }
        public string? PocoCloneHelperName { get; }

        public bool Equals(TypeModel other)
        {
            return string.Equals(Name, other.Name, StringComparison.Ordinal)
                && string.Equals(NonNullableName, other.NonNullableName, StringComparison.Ordinal)
                && string.Equals(RuntimeName, other.RuntimeName, StringComparison.Ordinal)
                && IsReferenceType == other.IsReferenceType
                && IsConfiglueType == other.IsConfiglueType
                && string.Equals(
                    PocoCloneHelperName,
                    other.PocoCloneHelperName,
                    StringComparison.Ordinal
                );
        }

        public override bool Equals(object? obj) => obj is TypeModel other && Equals(other);

        public override int GetHashCode()
        {
            var hash = StringHash(Name);
            hash = unchecked(hash * 31 + StringHash(NonNullableName));
            hash = unchecked(hash * 31 + StringHash(RuntimeName));
            hash = unchecked(hash * 31 + (IsReferenceType ? 1 : 0));
            hash = unchecked(hash * 31 + (IsConfiglueType ? 1 : 0));
            return unchecked(
                hash * 31 + (PocoCloneHelperName is null ? 0 : StringHash(PocoCloneHelperName))
            );
        }
    }

    private readonly struct PropertyModel : IEquatable<PropertyModel>
    {
        public PropertyModel(
            string name,
            TypeModel type,
            bool isRequired,
            string? jsonPropertyName,
            bool hasExplicitJsonPropertyName,
            string? environmentVariableName
        )
        {
            Name = name;
            Type = type;
            IsRequired = isRequired;
            JsonPropertyName = jsonPropertyName;
            HasExplicitJsonPropertyName = hasExplicitJsonPropertyName;
            EnvironmentVariableName = environmentVariableName;
        }

        public string Name { get; }
        public TypeModel Type { get; }
        public bool IsRequired { get; }
        public string? JsonPropertyName { get; }
        public bool HasExplicitJsonPropertyName { get; }
        public string? EnvironmentVariableName { get; }

        public bool Equals(PropertyModel other)
        {
            return string.Equals(Name, other.Name, StringComparison.Ordinal)
                && Type.Equals(other.Type)
                && IsRequired == other.IsRequired
                && string.Equals(JsonPropertyName, other.JsonPropertyName, StringComparison.Ordinal)
                && HasExplicitJsonPropertyName == other.HasExplicitJsonPropertyName
                && string.Equals(
                    EnvironmentVariableName,
                    other.EnvironmentVariableName,
                    StringComparison.Ordinal
                );
        }

        public override bool Equals(object? obj) => obj is PropertyModel other && Equals(other);

        public override int GetHashCode()
        {
            var hash = unchecked(
                StringComparer.Ordinal.GetHashCode(Name) * 31 + Type.GetHashCode()
            );
            hash = unchecked(hash * 31 + (IsRequired ? 1 : 0));
            hash = unchecked(
                hash * 31
                + (
                    JsonPropertyName is null
                        ? 0
                        : StringComparer.Ordinal.GetHashCode(JsonPropertyName)
                )
            );
            hash = unchecked(hash * 31 + (HasExplicitJsonPropertyName ? 1 : 0));
            return unchecked(
                hash * 31
                + (
                    EnvironmentVariableName is null
                        ? 0
                        : StringComparer.Ordinal.GetHashCode(EnvironmentVariableName)
                )
            );
        }
    }

    private readonly struct CollectionInfo : IEquatable<CollectionInfo>
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

        public CollectionKind Kind { get; }
        public CloneCollectionKind CloneKind { get; }
        public TypeModel ElementType { get; }
        public TypeModel? ValueType { get; }
        public string? NamedTypeDefinition { get; }
        public static CollectionInfo Unsupported { get; } =
            new(CollectionKind.Unsupported, CloneCollectionKind.Unsupported, default, null, null);

        public bool Equals(CollectionInfo other)
        {
            return Kind == other.Kind
                && CloneKind == other.CloneKind
                && ElementType.Equals(other.ElementType)
                && Nullable.Equals(ValueType, other.ValueType)
                && string.Equals(
                    NamedTypeDefinition,
                    other.NamedTypeDefinition,
                    StringComparison.Ordinal
                );
        }

        public override bool Equals(object? obj) => obj is CollectionInfo other && Equals(other);

        public override int GetHashCode()
        {
            var hash = unchecked((int)Kind * 31 + (int)CloneKind);
            hash = unchecked(hash * 31 + ElementType.GetHashCode());
            hash = unchecked(hash * 31 + (ValueType?.GetHashCode() ?? 0));
            return unchecked(
                hash * 31
                + (
                    NamedTypeDefinition is null
                        ? 0
                        : StringComparer.Ordinal.GetHashCode(NamedTypeDefinition)
                )
            );
        }
    }

    private readonly struct MemberModel : IEquatable<MemberModel>
    {
        public MemberModel(
            int id,
            PropertyModel property,
            TypeModel? childModel,
            int mergeMode,
            CollectionInfo collection,
            TypeModel? mergeStrategyType
        )
        {
            Id = id;
            Property = property;
            ChildModel = childModel;
            MergeMode = mergeMode;
            Collection = collection;
            MergeStrategyType = mergeStrategyType;
        }

        public int Id { get; }
        public PropertyModel Property { get; }
        public TypeModel? ChildModel { get; }
        public int MergeMode { get; }
        public CollectionInfo Collection { get; }
        public TypeModel? MergeStrategyType { get; }

        public bool Equals(MemberModel other)
        {
            return Id == other.Id
                && Property.Equals(other.Property)
                && Nullable.Equals(ChildModel, other.ChildModel)
                && MergeMode == other.MergeMode
                && Collection.Equals(other.Collection)
                && Nullable.Equals(MergeStrategyType, other.MergeStrategyType);
        }

        public override bool Equals(object? obj) => obj is MemberModel other && Equals(other);

        public override int GetHashCode()
        {
            var hash = unchecked(Id * 31 + Property.GetHashCode());
            hash = unchecked(hash * 31 + (ChildModel?.GetHashCode() ?? 0));
            hash = unchecked(hash * 31 + MergeMode);
            hash = unchecked(hash * 31 + Collection.GetHashCode());
            return unchecked(hash * 31 + (MergeStrategyType?.GetHashCode() ?? 0));
        }
    }

    private readonly struct ModelInfo : IEquatable<ModelInfo>
    {
        public ModelInfo(
            string name,
            string modelTypeName,
            string fullyQualifiedName,
            string @namespace,
            bool isGlobalNamespace,
            bool isStruct,
            bool isRecord,
            bool isPublic,
            string modelId,
            int version
        )
        {
            Name = name;
            ModelTypeName = modelTypeName;
            FullyQualifiedName = fullyQualifiedName;
            Namespace = @namespace;
            IsGlobalNamespace = isGlobalNamespace;
            IsStruct = isStruct;
            IsRecord = isRecord;
            IsPublic = isPublic;
            ModelId = modelId;
            Version = version;
        }

        public string Name { get; }
        public string ModelTypeName { get; }
        public string FullyQualifiedName { get; }
        public string Namespace { get; }
        public bool IsGlobalNamespace { get; }
        public bool IsStruct { get; }
        public bool IsRecord { get; }
        public bool IsPublic { get; }
        public string ModelId { get; }
        public int Version { get; }

        public bool Equals(ModelInfo other)
        {
            return string.Equals(Name, other.Name, StringComparison.Ordinal)
                && string.Equals(ModelTypeName, other.ModelTypeName, StringComparison.Ordinal)
                && string.Equals(
                    FullyQualifiedName,
                    other.FullyQualifiedName,
                    StringComparison.Ordinal
                )
                && string.Equals(Namespace, other.Namespace, StringComparison.Ordinal)
                && IsGlobalNamespace == other.IsGlobalNamespace
                && IsStruct == other.IsStruct
                && IsRecord == other.IsRecord
                && IsPublic == other.IsPublic
                && string.Equals(ModelId, other.ModelId, StringComparison.Ordinal)
                && Version == other.Version;
        }

        public override bool Equals(object? obj) => obj is ModelInfo other && Equals(other);

        public override int GetHashCode()
        {
            var hash = StringComparer.Ordinal.GetHashCode(Name);
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(ModelTypeName));
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(FullyQualifiedName));
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(Namespace));
            hash = unchecked(hash * 31 + (IsGlobalNamespace ? 1 : 0));
            hash = unchecked(hash * 31 + (IsStruct ? 1 : 0));
            hash = unchecked(hash * 31 + (IsRecord ? 1 : 0));
            hash = unchecked(hash * 31 + (IsPublic ? 1 : 0));
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(ModelId));
            return unchecked(hash * 31 + Version);
        }
    }

    private readonly struct PreviousMemberMapping : IEquatable<PreviousMemberMapping>
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

        public MemberModel CurrentMember { get; }
        public MemberModel PreviousMember { get; }
        public bool HasSameType { get; }
        public bool CanMigrateChild { get; }

        public bool Equals(PreviousMemberMapping other)
        {
            return CurrentMember.Equals(other.CurrentMember)
                && PreviousMember.Equals(other.PreviousMember)
                && HasSameType == other.HasSameType
                && CanMigrateChild == other.CanMigrateChild;
        }

        public override bool Equals(object? obj) =>
            obj is PreviousMemberMapping other && Equals(other);

        public override int GetHashCode()
        {
            return unchecked(
                (
                    (CurrentMember.GetHashCode() * 31 + PreviousMember.GetHashCode()) * 31
                    + (HasSameType ? 1 : 0)
                ) * 31
                + (CanMigrateChild ? 1 : 0)
            );
        }
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

    private sealed class GenerationAnalysis : IEquatable<GenerationAnalysis>
    {
        public GenerationAnalysis(
            string? hintName,
            ModelInfo? model,
            ImmutableArray<MemberModel> members,
            ImmutableArray<PreviousModelInfo> previousModels,
            ImmutableArray<PocoCloneModel> pocoCloneModels,
            ImmutableArray<GeneratorDiagnosticInfo> diagnostics
        )
        {
            HintName = hintName;
            Model = model;
            Members = members;
            PreviousModels = previousModels;
            PocoCloneModels = pocoCloneModels;
            Diagnostics = diagnostics;
        }

        public string? HintName { get; }
        public ModelInfo? Model { get; }
        public ImmutableArray<MemberModel> Members { get; }
        public ImmutableArray<PreviousModelInfo> PreviousModels { get; }
        public ImmutableArray<PocoCloneModel> PocoCloneModels { get; }
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
            member.Property.IsRequired,
            jsonPropertyName,
            hasExplicitJsonPropertyName,
            GetEnvironmentVariableName(member.Property, cancellationToken)
        );
        TypeModel? childModel = null;
        if (member.ChildModel is not null)
        {
            childModel = CreateTypeModel(member.ChildModel, cancellationToken);
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
            mergeStrategyType
        );
    }

    private static CollectionInfo CreateCollectionInfo(
        SymbolCollectionInfo collection,
        CancellationToken cancellationToken
    )
    {
        if (collection.Kind == CollectionKind.Unsupported)
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
            CreateModelInfo(pocoType, string.Empty, InitialSchemaVersion, cancellationToken),
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
            model.IsRecord,
            model.DeclaredAccessibility == Accessibility.Public,
            modelId,
            version
        );
    }

    private static int StringHash(string? value) =>
        value is null ? 0 : StringComparer.Ordinal.GetHashCode(value);

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
