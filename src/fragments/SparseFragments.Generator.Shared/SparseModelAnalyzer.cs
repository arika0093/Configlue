using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparseFragments.Generator.Shared;

internal sealed record SparseGeneratorConfig(
    string ModelAttributeMetadataName,
    string MergeAttributeMetadataName,
    string MergeStrategyBaseMetadataName,
    string CloneReferenceSafeAttributeMetadataName
);

internal static class SparseModelAnalyzer
{
    private const int CustomMergeMode = 4;

    public static SparseGenerationAnalysis Analyze(
        INamedTypeSymbol model,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var location = model.Locations.FirstOrDefault();
        var declaration = model
            .DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(cancellationToken))
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (declaration is null || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            return Failure(SparseDiagnosticIds.MustBePartial, location, model.Name);
        }

        if (
            model.ContainingType is not null
            || model.Arity != 0
            || (model.TypeKind != TypeKind.Class && model.TypeKind != TypeKind.Struct)
            || model.IsAbstract
        )
        {
            return Failure(SparseDiagnosticIds.UnsupportedModel, location, model.Name);
        }

        if (
            model.TypeKind == TypeKind.Class
            && ModelConstructorBinding.AnalyzeRoot(model, cancellationToken) is null
        )
        {
            return Failure(SparseDiagnosticIds.MissingConstructor, location, model.Name);
        }

        var members = GetMembers(model, config, cancellationToken).ToImmutableArray();
        var diagnostics = ImmutableArray.CreateBuilder<SparseGeneratorDiagnostic>();

        foreach (
            var member in ModelConstructionPlan.UnsupportedRequiredMembers(
                model,
                members.Select(static member => member.Property),
                cancellationToken
            )
        )
            diagnostics.Add(
                new SparseGeneratorDiagnostic(
                    SparseDiagnosticIds.UnsupportedRequired,
                    member.Locations.FirstOrDefault(),
                    member.Name
                )
            );

        foreach (var property in UnsupportedStructuralMembers(model, config, cancellationToken))
            diagnostics.Add(
                new SparseGeneratorDiagnostic(
                    SparseDiagnosticIds.UnsupportedStructural,
                    property.Locations.FirstOrDefault(),
                    property.Name
                )
            );

        foreach (var property in UnsupportedCloneMembers(model, config, cancellationToken))
            diagnostics.Add(
                new SparseGeneratorDiagnostic(
                    SparseDiagnosticIds.UnsupportedClone,
                    property.Locations.FirstOrDefault(),
                    property.Name
                )
            );

        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                member.MergeMode == CustomMergeMode
                && (
                    member.MergeStrategyType is null
                    || !IsValidMergeStrategy(
                        member.MergeStrategyType,
                        member.Property.Type,
                        member.ChildModel is not null,
                        config,
                        cancellationToken
                    )
                )
            )
            {
                diagnostics.Add(
                    new SparseGeneratorDiagnostic(
                        SparseDiagnosticIds.InvalidMergeStrategy,
                        member.Property.Locations.FirstOrDefault(),
                        member.Property.Name
                    )
                );
            }

            if (
                SparseMergeValidation.GetUnsupportedReason(
                    member.MergeMode,
                    member.ChildModel is not null,
                    member.Collection.Kind
                )
                is not null
            )
            {
                diagnostics.Add(
                    new SparseGeneratorDiagnostic(
                        SparseDiagnosticIds.UnsupportedMerge,
                        member.Property.Locations.FirstOrDefault(),
                        member.Property.Name
                    )
                );
            }
        }

        if (diagnostics.Count > 0)
        {
            return new SparseGenerationAnalysis(
                null,
                ImmutableArray<SparseMemberModel>.Empty,
                ImmutableArray<SparsePocoCloneModel>.Empty,
                ImmutableArray<SparseStructuralModel>.Empty,
                diagnostics.ToImmutable()
            );
        }

        var fullyQualifiedName = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var hintName =
            SparseNaming.Sanitize(fullyQualifiedName, cancellationToken)
            + "_"
            + SparseNaming.GetStableTypeHash(fullyQualifiedName, cancellationToken)
            + SparseWellKnownNames.HintNameSuffix;
        var memberModels = CreateMemberModels(members, config, cancellationToken);
        var pocoCloneModels = GetPocoCloneTypes(members, config, cancellationToken)
            .Select(pocoType => CreatePocoCloneModel(pocoType, config, cancellationToken))
            .ToImmutableArray();
        var structuralModels = CollectStructuralTypes(members, config, cancellationToken)
            .Select(type => CreateStructuralModel(type, config, cancellationToken))
            .ToImmutableArray();

        return new SparseGenerationAnalysis(
            CreateModelInfo(model, hintName, cancellationToken),
            memberModels,
            pocoCloneModels,
            structuralModels,
            ImmutableArray<SparseGeneratorDiagnostic>.Empty
        );
    }

    public static IEnumerable<IPropertySymbol> UnsupportedCloneMembers(
        INamedTypeSymbol model,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (
            var property in GetMembers(model, config, cancellationToken)
                .Select(static member => member.Property)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                property
                    .GetAttributes()
                    .Any(attribute =>
                        attribute.AttributeClass?.ToDisplayString()
                        == config.CloneReferenceSafeAttributeMetadataName
                    )
            )
                continue;
            if (ContainsUnregisterableCloneCycle(property.Type, config, cancellationToken))
            {
                yield return property;
                continue;
            }
            var types = new Stack<(ITypeSymbol Type, bool ReferenceSafe)>();
            types.Push((property.Type, false));
            while (types.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (type, referenceSafe) = types.Pop();
                if (referenceSafe)
                    continue;
                if (type is INamedTypeSymbol alreadySeen && visited.Contains(alreadySeen))
                    continue;
                if (!IsCloneSupported(type, config, cancellationToken, visited))
                {
                    yield return property;
                    break;
                }
                foreach (var nested in GetCloneChildren(type, config, cancellationToken))
                    types.Push(nested);
            }
            visited.Clear();
        }
    }

    private static bool ContainsUnregisterableCloneCycle(
        ITypeSymbol type,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    ) =>
        ContainsUnregisterableCloneCycle(
            type,
            config,
            cancellationToken,
            new List<INamedTypeSymbol>()
        );

    private static bool ContainsUnregisterableCloneCycle(
        ITypeSymbol type,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken,
        List<INamedTypeSymbol> path
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (type is IArrayTypeSymbol array)
            return ContainsUnregisterableCloneCycle(
                array.ElementType,
                config,
                cancellationToken,
                path
            );
        if (type is not INamedTypeSymbol named)
            return false;
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return ContainsUnregisterableCloneCycle(
                named.TypeArguments[0],
                config,
                cancellationToken,
                path
            );
        var collection = SparseCollectionAnalyzer.GetCollectionInfo(named);
        if (collection.CloneKind != SparseCloneCollectionKind.Unsupported)
        {
            if (
                collection.ElementType is not null
                && ContainsUnregisterableCloneCycle(
                    collection.ElementType,
                    config,
                    cancellationToken,
                    path
                )
            )
                return true;
            return collection.ValueType is not null
                && ContainsUnregisterableCloneCycle(
                    collection.ValueType,
                    config,
                    cancellationToken,
                    path
                );
        }
        if (
            !IsFragmentModel(named, config, cancellationToken)
            && ClassifyStructuralType(named, config, cancellationToken)
                != StructuralTypeKind.StructuralObject
        )
            return false;

        var cycleStart = path.FindIndex(candidate =>
            SymbolEqualityComparer.Default.Equals(candidate, named)
        );
        if (cycleStart >= 0)
        {
            for (var index = cycleStart; index < path.Count; index++)
                if (HasPreRegistrationCloneCycle(path[index], config, cancellationToken))
                    return true;
            return false;
        }

        path.Add(named);
        foreach (
            var property in GetMembers(named, config, cancellationToken)
                .Select(static member => member.Property)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                property
                    .GetAttributes()
                    .Any(attribute =>
                        attribute.AttributeClass?.ToDisplayString()
                        == config.CloneReferenceSafeAttributeMetadataName
                    )
            )
                continue;
            if (ContainsUnregisterableCloneCycle(property.Type, config, cancellationToken, path))
                return true;
        }
        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static bool HasPreRegistrationCloneCycle(
        INamedTypeSymbol model,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        var fragmentModel = IsFragmentModel(model, config, cancellationToken);
        var members = GetMembers(model, config, cancellationToken).ToImmutableArray();
        if (model.TypeKind == TypeKind.Struct)
            return members.Any(member =>
                CanReachCloneType(member.Property.Type, model, config, cancellationToken)
            );

        var constructor = fragmentModel
            ? ModelConstructorBinding.AnalyzeRoot(model, cancellationToken)
            : ModelConstructorBinding.AnalyzeStructural(model, cancellationToken);
        if (constructor is null)
            return false;

        IEnumerable<SparseSymbolMemberModel> preRegistrationMembers;
        if (fragmentModel)
        {
            var generatedMembers = CreateMemberModels(members, config, cancellationToken);
            if (
                constructor.Parameters.IsEmpty
                && ModelConstructionPlan.ForMembers(generatedMembers).CanOverlayAfterConstruction
            )
                return false;
            preRegistrationMembers = members;
        }
        else
        {
            if (constructor.Parameters.IsEmpty)
                return false;
            var boundNames = new HashSet<string>(
                constructor.Parameters.Select(static parameter => parameter.PropertyName),
                StringComparer.Ordinal
            );
            preRegistrationMembers = members.Where(member =>
                boundNames.Contains(member.Property.Name)
            );
        }

        return preRegistrationMembers.Any(member =>
            CanReachCloneType(member.Property.Type, model, config, cancellationToken)
        );
    }

    private static bool CanReachCloneType(
        ITypeSymbol type,
        INamedTypeSymbol target,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        var pending = new Stack<ITypeSymbol>();
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        pending.Push(type);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (SymbolEqualityComparer.Default.Equals(current, target))
                return true;
            if (!visited.Add(current))
                continue;
            foreach (
                var (nested, referenceSafe) in GetCloneChildren(current, config, cancellationToken)
            )
                if (!referenceSafe)
                    pending.Push(nested);
        }
        return false;
    }

    private static bool IsCloneSupported(
        ITypeSymbol type,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken,
        HashSet<ITypeSymbol> visited
    )
    {
        if (type is IArrayTypeSymbol array)
            return IsCloneSupported(array.ElementType, config, cancellationToken, visited);
        if (type is not INamedTypeSymbol named)
            return type.IsValueType;
        if (!visited.Add(named))
            return true;
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return IsCloneSupported(named.TypeArguments[0], config, cancellationToken, visited);
        if (named.TypeKind == TypeKind.Enum)
            return true;
        if (named.SpecialType != SpecialType.None)
            return named.SpecialType != SpecialType.System_Object;
        var collection = SparseCollectionAnalyzer.GetCollectionInfo(named);
        if (collection.CloneKind != SparseCloneCollectionKind.Unsupported)
            return true;
        var fullName = named
            .WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            .ToDisplayString();
        if (fullName is "System.Uri" or "System.Version" or "System.Type")
            return true;
        if (named.IsValueType)
        {
            if (IsFrameworkType(named))
                return IsSafeToCopyValue(
                    named,
                    new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)
                );
            return IsSafeToCopyValue(
                named,
                new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)
            );
        }
        if (IsFragmentModel(named, config, cancellationToken))
            return true;
        if (IsFrameworkType(named))
            return false;
        return ClassifyStructuralType(named, config, cancellationToken)
                == StructuralTypeKind.StructuralObject
            && IsAccessibleForGeneration(named);
    }

    private static IEnumerable<(ITypeSymbol Type, bool ReferenceSafe)> GetCloneChildren(
        ITypeSymbol type,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        if (type is IArrayTypeSymbol array)
        {
            yield return (array.ElementType, false);
            yield break;
        }
        if (type is not INamedTypeSymbol named)
            yield break;
        var collection = SparseCollectionAnalyzer.GetCollectionInfo(named);
        if (collection.CloneKind != SparseCloneCollectionKind.Unsupported)
        {
            if (collection.ElementType is not null)
                yield return (collection.ElementType, false);
            if (collection.ValueType is not null)
                yield return (collection.ValueType, false);
            yield break;
        }
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            yield return (named.TypeArguments[0], false);
            yield break;
        }
        if (named.IsValueType && !IsFrameworkType(named))
        {
            foreach (var field in named.GetMembers().OfType<IFieldSymbol>())
                if (!field.IsStatic && field.AssociatedSymbol is not IPropertySymbol)
                    yield return (field.Type, false);
            foreach (var property in named.GetMembers().OfType<IPropertySymbol>())
                if (!property.IsStatic && IsCloneRelevantProperty(property))
                    yield return (
                        property.Type,
                        property
                            .GetAttributes()
                            .Any(attribute =>
                                attribute.AttributeClass?.ToDisplayString()
                                == config.CloneReferenceSafeAttributeMetadataName
                            )
                    );
            yield break;
        }
        if (
            !IsFrameworkType(named)
            && (
                IsFragmentModel(named, config, cancellationToken)
                || ClassifyStructuralType(named, config, cancellationToken)
                    == StructuralTypeKind.StructuralObject
            )
        )
            foreach (
                var member in GetMembers(named, config, cancellationToken)
                    .Select(static member => member.Property)
            )
                yield return (
                    member.Type,
                    member
                        .GetAttributes()
                        .Any(attribute =>
                            attribute.AttributeClass?.ToDisplayString()
                            == config.CloneReferenceSafeAttributeMetadataName
                        )
                );
    }

    private static bool IsCloneRelevantProperty(IPropertySymbol property) =>
        !property.IsIndexer
        && property.GetMethod is not null
        && property.DeclaredAccessibility == Accessibility.Public;

    private static bool IsSafeToCopyValue(ITypeSymbol type, HashSet<ITypeSymbol> visited)
    {
        if (type is IArrayTypeSymbol)
            return false;
        if (type is not INamedTypeSymbol named)
            return type.IsValueType;
        if (named.SpecialType == SpecialType.System_String)
            return true;
        if (named.SpecialType != SpecialType.None || named.TypeKind == TypeKind.Enum)
            return named.IsValueType;
        if (!named.IsValueType)
            return named.ToDisplayString() is "System.Uri" or "System.Version" or "System.Type";
        if (IsFrameworkType(named))
        {
            if (named.TypeArguments.Length == 0)
                return true;
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return IsSafeToCopyValue(named.TypeArguments[0], visited);
            return !named.TypeArguments.Any(argument => !IsSafeToCopyValue(argument, visited));
        }
        if (!visited.Add(named))
            return true;
        return !named
                .GetMembers()
                .OfType<IFieldSymbol>()
                .Any(field =>
                    !field.IsStatic
                    && field.AssociatedSymbol is not IPropertySymbol
                    && !IsSafeToCopyValue(field.Type, visited)
                )
            && !named
                .GetMembers()
                .OfType<IPropertySymbol>()
                .Any(property =>
                    !property.IsStatic
                    && IsCloneRelevantProperty(property)
                    && !property
                        .GetAttributes()
                        .Any(attribute =>
                            attribute.AttributeClass?.ToDisplayString()
                                == "SparseFragments.SparseCloneReferenceSafeAttribute"
                            || attribute.AttributeClass?.ToDisplayString()
                                == "Configlue.ConfiglueCloneReferenceSafeAttribute"
                        )
                    && !IsSafeToCopyValue(property.Type, visited)
                );
    }

    private static SparseGenerationAnalysis Failure(
        string descriptorId,
        Location? location,
        string argument
    ) =>
        new(
            null,
            ImmutableArray<SparseMemberModel>.Empty,
            ImmutableArray<SparsePocoCloneModel>.Empty,
            ImmutableArray<SparseStructuralModel>.Empty,
            ImmutableArray.Create(new SparseGeneratorDiagnostic(descriptorId, location, argument))
        );

    public static IEnumerable<IPropertySymbol> UnsupportedStructuralMembers(
        INamedTypeSymbol model,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        var pending = new Stack<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        pending.Push(model);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
                continue;
            foreach (
                var property in GetMembers(current, config, cancellationToken)
                    .Select(static member => member.Property)
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    property.Type
                        is not INamedTypeSymbol
                        {
                            TypeKind: TypeKind.Class,
                            SpecialType: SpecialType.None
                        } type
                    || IsFrameworkType(type)
                    || IsFragmentModel(type, config, cancellationToken)
                )
                    continue;
                if (
                    SparseCollectionAnalyzer.GetCollectionInfo(type).CloneKind
                    != SparseCloneCollectionKind.Unsupported
                )
                    continue;
                if (IsStructuralType(type, config, cancellationToken))
                {
                    pending.Push(type);
                    continue;
                }
                var replace = property
                    .GetAttributes()
                    .Any(attribute =>
                        attribute.AttributeClass?.ToDisplayString()
                            == config.MergeAttributeMetadataName
                        && attribute.ConstructorArguments.FirstOrDefault().Value is int mode
                        && mode == 0
                    );
                if (!replace)
                    yield return property;
            }
        }
    }

    internal static IEnumerable<IPropertySymbol> GetReadableProperties(
        INamedTypeSymbol model,
        CancellationToken cancellationToken,
        bool requirePublicSetter = false
    )
    {
        var hierarchy = new Stack<INamedTypeSymbol>();
        for (
            var current = model;
            current is not null && current.SpecialType != SpecialType.System_Object;
            current = current.BaseType
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            hierarchy.Push(current);
        }

        var properties = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        while (hierarchy.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var property in hierarchy.Pop().GetMembers().OfType<IPropertySymbol>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    property.IsStatic
                    || property.IsIndexer
                    || property.DeclaredAccessibility != Accessibility.Public
                    || property.GetMethod?.DeclaredAccessibility != Accessibility.Public
                    || (
                        requirePublicSetter
                        && property.SetMethod?.DeclaredAccessibility != Accessibility.Public
                    )
                )
                {
                    continue;
                }

                properties[property.Name] = property;
            }
        }

        return properties.Values.OrderBy(static property => property.Name, StringComparer.Ordinal);
    }

    internal static IEnumerable<SparseSymbolMemberModel> GetMembers(
        INamedTypeSymbol model,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        var constructor = IsFragmentModel(model, config, cancellationToken)
            ? ModelConstructorBinding.AnalyzeRoot(model, cancellationToken)
            : ModelConstructorBinding.AnalyzeStructural(model, cancellationToken);
        var index = 0;
        foreach (
            var property in GetReadableProperties(model, cancellationToken)
                .Where(property =>
                    property.SetMethod?.DeclaredAccessibility == Accessibility.Public
                    || (
                        property.SetMethod is null
                        && constructor is not null
                        && constructor.Parameters.Any(parameter =>
                            parameter.PropertyName == property.Name
                        )
                    )
                )
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child =
                IsFragmentModel(property.Type, config, cancellationToken)
                || IsStructuralType(property.Type, config, cancellationToken)
                    ? (INamedTypeSymbol)property.Type
                    : null;
            var mode = child is not null ? 1 : 0;
            AttributeData? merge = null;
            foreach (var attribute in property.GetAttributes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    attribute.AttributeClass?.ToDisplayString() == config.MergeAttributeMetadataName
                )
                {
                    merge = attribute;
                    break;
                }
            }

            INamedTypeSymbol? mergeStrategyType = null;
            if (
                merge?.ConstructorArguments.FirstOrDefault() is
                { Kind: TypedConstantKind.Type } strategyConstant
            )
            {
                mergeStrategyType = strategyConstant.Value as INamedTypeSymbol;
                mode = CustomMergeMode;
            }
            else if (merge?.ConstructorArguments.FirstOrDefault().Value is int requestedMode)
            {
                mode = requestedMode;
            }

            if (mode is < 0 or > CustomMergeMode)
            {
                mode = int.MaxValue;
            }

            yield return new SparseSymbolMemberModel(
                index++,
                property,
                child,
                mode,
                SparseCollectionAnalyzer.GetCollectionInfo(property.Type),
                mergeStrategyType
            );
        }
    }

    private static bool IsFragmentModel(
        ITypeSymbol type,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        if (
            type
            is not INamedTypeSymbol
            {
                TypeKind: TypeKind.Class or TypeKind.Struct,
                IsAbstract: false,
            } named
        )
        {
            return false;
        }

        foreach (var attribute in named.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass?.ToDisplayString() == config.ModelAttributeMetadataName)
            {
                return true;
            }
        }

        return false;
    }

    private enum StructuralTypeKind
    {
        RootModel,
        StructuralObject,
        Collection,
        Scalar,
    }

    private static StructuralTypeKind ClassifyStructuralType(
        ITypeSymbol type,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        if (
            type is IArrayTypeSymbol
            || (
                type is INamedTypeSymbol arrayLike
                && SparseCollectionAnalyzer.GetCollectionInfo(arrayLike).Kind
                    != SparseCollectionKind.Unsupported
            )
        )
        {
            return StructuralTypeKind.Collection;
        }

        if (
            type
                is not INamedTypeSymbol
                {
                    TypeKind: TypeKind.Class,
                    IsAbstract: false,
                    Arity: 0,
                } named
            || named.SpecialType != SpecialType.None
            || IsFrameworkType(named)
            || ModelConstructorBinding.AnalyzeStructural(named, cancellationToken) is null
        )
        {
            return StructuralTypeKind.Scalar;
        }

        if (IsFragmentModel(named, config, cancellationToken))
        {
            return StructuralTypeKind.RootModel;
        }

        if (
            HasUnsupportedPocoMembers(named, cancellationToken)
            || !SparseModelAnalyzer.GetReadableProperties(named, cancellationToken).Any()
        )
        {
            return StructuralTypeKind.Scalar;
        }

        return StructuralTypeKind.StructuralObject;
    }

    private static bool IsStructuralType(
        ITypeSymbol type,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    ) =>
        type is INamedTypeSymbol named
        && ClassifyStructuralType(type, config, cancellationToken)
            == StructuralTypeKind.StructuralObject
        && IsAccessibleForGeneration(named);

    private static bool IsFrameworkType(INamedTypeSymbol type)
    {
        var namespaceName = type.ContainingNamespace.ToDisplayString();
        if (
            namespaceName == "System"
            || namespaceName.StartsWith("System.", StringComparison.Ordinal)
            || namespaceName == "Microsoft"
            || namespaceName.StartsWith("Microsoft.", StringComparison.Ordinal)
        )
        {
            return true;
        }

        var assemblyName = type.ContainingAssembly?.Name;
        return assemblyName is not null
            && (
                assemblyName.StartsWith("System.", StringComparison.Ordinal)
                || assemblyName.StartsWith("Microsoft.", StringComparison.Ordinal)
            );
    }

    private static bool IsAccessibleForGeneration(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

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
        SparseGeneratorConfig config,
        CancellationToken cancellationToken,
        out INamedTypeSymbol pocoType
    )
    {
        if (
            type is INamedTypeSymbol named
            && ClassifyStructuralType(type, config, cancellationToken)
                == StructuralTypeKind.StructuralObject
            && IsAccessibleForClone(named)
        )
        {
            var members = GetMembers(named, config, cancellationToken).ToArray();
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
        var name = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            .ToDisplayString(SparseNaming.TypeFormat);
        var assembly = type.ContainingAssembly?.Name ?? string.Empty;
        return SparseWellKnownNames.StructuralHostPrefix
            + SparseNaming.GetStableTypeHash(assembly + "|" + name, cancellationToken);
    }

    private static bool HasUnsupportedPocoMembers(
        INamedTypeSymbol pocoType,
        CancellationToken cancellationToken
    ) => ModelConstructionPlan.HasUnsupportedStructuralMembers(pocoType, cancellationToken);

    private static ImmutableArray<INamedTypeSymbol> GetPocoCloneTypes(
        ImmutableArray<SparseSymbolMemberModel> members,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        var result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<ITypeSymbol>();
        foreach (var type in members.Select(static member => member.Property.Type))
        {
            pending.Push(type);
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = pending.Pop();
            var collection = SparseCollectionAnalyzer.GetCollectionInfo(type);
            if (collection.CloneKind != SparseCloneCollectionKind.Unsupported)
            {
                if (collection.ElementType is not null)
                {
                    pending.Push(collection.ElementType);
                }

                if (collection.ValueType is not null)
                {
                    pending.Push(collection.ValueType);
                }

                continue;
            }

            if (
                !TryGetPocoCloneType(type, config, cancellationToken, out var poco)
                || !seen.Add(poco)
            )
            {
                continue;
            }

            result.Add(poco);
            foreach (
                var nestedType in GetMembers(poco, config, cancellationToken)
                    .Select(static member => member.Property.Type)
            )
            {
                pending.Push(nestedType);
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<INamedTypeSymbol> CollectStructuralTypes(
        ImmutableArray<SparseSymbolMemberModel> members,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        var result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<INamedTypeSymbol>();
        foreach (var child in members.Select(static member => member.ChildModel))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child is not null && IsStructuralType(child, config, cancellationToken))
            {
                pending.Push(child);
            }
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = pending.Pop();
            if (!seen.Add(type))
            {
                continue;
            }

            result.Add(type);
            foreach (
                var nested in GetMembers(type, config, cancellationToken)
                    .Select(static member => member.ChildModel)
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (nested is not null && IsStructuralType(nested, config, cancellationToken))
                {
                    pending.Push(nested);
                }
            }
        }

        return result.ToImmutable();
    }

    private static SparseStructuralModel CreateStructuralModel(
        INamedTypeSymbol type,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    ) =>
        new(
            StructuralHostName(type, cancellationToken),
            SparseNaming.NonNullableTypeName(type),
            CreateMemberModels(
                GetMembers(type, config, cancellationToken).ToImmutableArray(),
                config,
                cancellationToken
            ),
            ModelConstructorBinding.AnalyzeStructural(type, cancellationToken)
        );

    private static ImmutableArray<SparseMemberModel> CreateMemberModels(
        ImmutableArray<SparseSymbolMemberModel> members,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        var result = ImmutableArray.CreateBuilder<SparseMemberModel>(members.Length);
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(CreateMemberModel(member, config, cancellationToken));
        }

        return result.ToImmutable();
    }

    private static SparseMemberModel CreateMemberModel(
        SparseSymbolMemberModel member,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        var jsonPropertyName = SparseJsonNaming.GetJsonPropertyName(
            member.Property,
            cancellationToken,
            out var hasExplicitJsonPropertyName
        );
        var property = new SparsePropertyModel(
            member.Property.Name,
            CreateTypeModel(member.Property.Type, config, cancellationToken),
            member.Property.SetMethod?.IsInitOnly == true,
            RoslynSymbolCompat.IsRequired(member.Property),
            member.Property.SetMethod is null,
            jsonPropertyName,
            hasExplicitJsonPropertyName,
            SparseJsonNaming.GetJsonIgnoreCondition(member.Property, cancellationToken)
        );
        SparseTypeModel? childModel = null;
        string? childFragmentType = null;
        var childIsStructural = false;
        var childIsReferenceType = true;
        if (member.ChildModel is not null)
        {
            childModel = CreateTypeModel(member.ChildModel, config, cancellationToken);
            childIsReferenceType = member.ChildModel.IsReferenceType;
            childIsStructural = !IsFragmentModel(member.ChildModel, config, cancellationToken);
            var host = childIsStructural
                ? StructuralHostName(member.ChildModel, cancellationToken)
                : SparseNaming.NonNullableTypeName(member.ChildModel);
            childFragmentType = host + "." + SparseWellKnownNames.FragmentTypeName;
        }

        SparseTypeModel? mergeStrategyType = null;
        if (member.MergeStrategyType is not null)
        {
            mergeStrategyType = CreateTypeModel(
                member.MergeStrategyType,
                config,
                cancellationToken
            );
        }

        return new SparseMemberModel(
            member.Id,
            property,
            childModel,
            member.MergeMode,
            CreateCollectionInfo(member.Collection, config, cancellationToken),
            mergeStrategyType,
            childFragmentType,
            childIsStructural,
            childIsReferenceType
        );
    }

    private static SparseCollectionInfo CreateCollectionInfo(
        SparseSymbolCollectionInfo collection,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        if (
            collection.Kind == SparseCollectionKind.Unsupported
            && collection.CloneKind == SparseCloneCollectionKind.Unsupported
        )
        {
            return SparseCollectionInfo.Unsupported;
        }

        SparseTypeModel? valueType = null;
        if (collection.ValueType is not null)
        {
            valueType = CreateTypeModel(collection.ValueType, config, cancellationToken);
        }

        return new SparseCollectionInfo(
            collection.Kind,
            collection.CloneKind,
            CreateTypeModel(collection.ElementType, config, cancellationToken),
            valueType,
            collection.NamedType?.ConstructedFrom.ToDisplayString()
        );
    }

    private static SparseTypeModel CreateTypeModel(
        ITypeSymbol type,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isFragmentModel = IsFragmentModel(type, config, cancellationToken);
        string? pocoCloneHelperName = null;
        if (
            !isFragmentModel
            && TryGetPocoCloneType(type, config, cancellationToken, out var pocoType)
        )
        {
            var cloneTypeName = pocoType
                .WithNullableAnnotation(NullableAnnotation.NotAnnotated)
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            pocoCloneHelperName =
                SparseWellKnownNames.CloneHelperPrefix
                + SparseNaming.GetStableTypeHash(cloneTypeName, cancellationToken);
        }

        return new SparseTypeModel(
            SparseNaming.TypeName(type),
            SparseNaming.NonNullableTypeName(type),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.IsReferenceType,
            isFragmentModel,
            pocoCloneHelperName,
            type is INamedTypeSymbol named && (isFragmentModel || pocoCloneHelperName is not null)
                ? SparseNaming.PatchApiPrefix(
                    GetMembers(named, config, cancellationToken)
                        .Select(static member => member.Property.Name)
                )
                : string.Empty
        );
    }

    private static SparsePocoCloneModel CreatePocoCloneModel(
        INamedTypeSymbol pocoType,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        var typeName = pocoType
            .WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return new SparsePocoCloneModel(
            CreateModelInfo(pocoType, string.Empty, cancellationToken) with
            {
                Constructor = ModelConstructorBinding.AnalyzeStructural(
                    pocoType,
                    cancellationToken
                ),
            },
            SparseWellKnownNames.CloneHelperPrefix
                + SparseNaming.GetStableTypeHash(typeName, cancellationToken),
            CreateMemberModels(
                GetMembers(pocoType, config, cancellationToken).ToImmutableArray(),
                config,
                cancellationToken
            )
        );
    }

    private static SparseModelInfo CreateModelInfo(
        INamedTypeSymbol model,
        string hintName,
        CancellationToken cancellationToken
    ) =>
        new(
            model.Name,
            SparseNaming.NonNullableTypeName(model),
            model.ContainingNamespace.ToDisplayString(),
            model.ContainingNamespace.IsGlobalNamespace,
            model.TypeKind == TypeKind.Struct,
            model.IsRecord,
            hintName,
            ModelConstructorBinding.AnalyzeRoot(model, cancellationToken)
        );

    private static bool IsValidMergeStrategy(
        INamedTypeSymbol strategyType,
        ITypeSymbol memberType,
        bool isNestedModel,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        if (
            isNestedModel
            || strategyType.TypeKind != TypeKind.Class
            || strategyType.IsAbstract
            || strategyType.Arity != 0
        )
        {
            return false;
        }

        for (var current = strategyType; current is not null; current = current.ContainingType)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                current.DeclaredAccessibility
                is not (Accessibility.Public or Accessibility.Internal)
            )
            {
                return false;
            }
        }

        var hasConstructor = strategyType.InstanceConstructors.Any(static constructor =>
            constructor.Parameters.Length == 0
            && constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
        );
        if (!hasConstructor)
        {
            return false;
        }

        for (var current = strategyType; current is not null; current = current.BaseType)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                current.OriginalDefinition.ToDisplayString() == config.MergeStrategyBaseMetadataName
                && SymbolEqualityComparer.Default.Equals(current.TypeArguments[0], memberType)
            )
            {
                return true;
            }
        }

        return false;
    }
}
