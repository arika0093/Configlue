using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace SparseFragments.Generator.Shared;

/// <summary>Analyzes cloneability and clone reference cycles for model members.</summary>
internal static class SparseCloneAnalysis
{
    public static IEnumerable<IPropertySymbol> UnsupportedCloneMembers(
        INamedTypeSymbol model,
        SparseGeneratorConfig config,
        CancellationToken cancellationToken
    )
    {
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (
            var property in SparseModelDiscovery.GetMembers(model, config, cancellationToken)
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
            !SparseModelDiscovery.IsFragmentModel(named, config, cancellationToken)
            && SparseModelDiscovery.ClassifyStructuralType(named, config, cancellationToken)
                != SparseModelDiscovery.StructuralTypeKind.StructuralObject
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
            var property in SparseModelDiscovery.GetMembers(named, config, cancellationToken)
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
        var fragmentModel = SparseModelDiscovery.IsFragmentModel(model, config, cancellationToken);
        var members = SparseModelDiscovery.GetMembers(model, config, cancellationToken).ToImmutableArray();
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
            var generatedMembers = SparseModelDiscovery.CreateMemberModels(
                members,
                config,
                cancellationToken
            );
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
            if (SparseModelDiscovery.IsFrameworkType(named))
                return IsSafeToCopyValue(
                    named,
                    new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)
                );
            return IsSafeToCopyValue(
                named,
                new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)
            );
        }
        if (SparseModelDiscovery.IsFragmentModel(named, config, cancellationToken))
            return true;
        if (SparseModelDiscovery.IsFrameworkType(named))
            return false;
        return SparseModelDiscovery.ClassifyStructuralType(named, config, cancellationToken)
                == SparseModelDiscovery.StructuralTypeKind.StructuralObject
            && SparseModelDiscovery.IsAccessibleForGeneration(named);
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
        if (named.IsValueType && !SparseModelDiscovery.IsFrameworkType(named))
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
            !SparseModelDiscovery.IsFrameworkType(named)
            && (
                SparseModelDiscovery.IsFragmentModel(named, config, cancellationToken)
                || SparseModelDiscovery.ClassifyStructuralType(named, config, cancellationToken)
                    == SparseModelDiscovery.StructuralTypeKind.StructuralObject
            )
        )
            foreach (
                var member in SparseModelDiscovery.GetMembers(named, config, cancellationToken)
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
        if (SparseModelDiscovery.IsFrameworkType(named))
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
}
