using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    private static bool HasPublicParameterlessConstructor(
        INamedTypeSymbol model,
        CancellationToken cancellationToken
    )
    {
        foreach (var constructor in model.InstanceConstructors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                constructor.DeclaredAccessibility == Accessibility.Public
                && constructor.Parameters.Length == 0
            )
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<SymbolMemberModel> GetMembers(
        INamedTypeSymbol model,
        CancellationToken cancellationToken
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
                    || property.SetMethod?.DeclaredAccessibility != Accessibility.Public
                )
                {
                    continue;
                }

                properties[property.Name] = property;
            }
        }

        var index = 0;
        foreach (
            var property in properties.Values.OrderBy(
                static property => property.Name,
                StringComparer.Ordinal
            )
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = IsConfiglueModel(property.Type, cancellationToken)
                ? (INamedTypeSymbol)property.Type
                : null;
            var mode = child is not null ? 1 : 0;
            AttributeData? merge = null;
            foreach (var attribute in property.GetAttributes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attribute.AttributeClass?.ToDisplayString() == MergeAttributeName)
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

            yield return new SymbolMemberModel(
                index++,
                property,
                child,
                mode,
                GetCollectionInfo(property.Type),
                mergeStrategyType
            );
        }
    }

    private static ImmutableArray<SymbolPreviousModelInfo> GetPreviousModels(
        INamedTypeSymbol model,
        string modelId,
        int modelVersion,
        CancellationToken cancellationToken,
        ImmutableArray<GeneratorDiagnosticInfo>.Builder diagnostics
    )
    {
        var previousModels = ImmutableArray.CreateBuilder<SymbolPreviousModelInfo>();
        var seenVersions = new HashSet<int>();
        foreach (var attribute in model.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass?.ToDisplayString() != PreviousVersionAttributeName)
            {
                continue;
            }

            var previousModel =
                attribute.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol;
            var valid =
                previousModel is not null
                && previousModel.ContainingType is null
                && previousModel.Arity == 0
                && (
                    previousModel.TypeKind == TypeKind.Class
                    || previousModel.TypeKind == TypeKind.Struct
                )
                && HasConfiglueModelAttribute(previousModel, cancellationToken);
            var previousVersion = previousModel is null
                ? InitialSchemaVersion
                : GetModelVersion(previousModel, cancellationToken);
            if (
                !valid
                || previousModel is null
                || previousVersion < InitialSchemaVersion
                || previousVersion >= modelVersion
                || !string.Equals(
                    GetModelId(previousModel, cancellationToken),
                    modelId,
                    StringComparison.Ordinal
                )
                || !seenVersions.Add(previousVersion)
            )
            {
                diagnostics.Add(
                    GeneratorDiagnosticInfo.Create(
                        InvalidPreviousVersion,
                        attribute
                            .ApplicationSyntaxReference?.GetSyntax(cancellationToken)
                            .GetLocation(),
                        previousModel?.Name ?? "<unknown>",
                        model.Name
                    )
                );
                continue;
            }

            previousModels.Add(
                new SymbolPreviousModelInfo(
                    previousModel,
                    GetMembers(previousModel, cancellationToken).ToImmutableArray()
                )
            );
        }

        return previousModels.ToImmutable();
    }
}
