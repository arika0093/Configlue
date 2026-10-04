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

/// <summary>Orchestrates model analysis: shape validation, member discovery and diagnostics.</summary>
/// <remarks>
/// Each analysis phase is owned by a focused component: <see cref="SparseModelDiscovery"/>
/// for member/model construction, <see cref="SparseCloneAnalysis"/> for cloneability and
/// cycles, and <see cref="SparseModelDiagnostics"/> for validation diagnostics.
/// </remarks>
internal static class SparseModelAnalyzer
{
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

        var members = SparseModelDiscovery.GetMembers(model, config, cancellationToken).ToImmutableArray();
        var diagnostics = ImmutableArray.CreateBuilder<SparseGeneratorDiagnostic>();

        SparseModelDiagnostics.CollectMemberDiagnostics(
            model,
            members,
            config,
            diagnostics,
            cancellationToken
        );

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
        var memberModels = SparseModelDiscovery.CreateMemberModels(members, config, cancellationToken);
        var pocoCloneModels = SparseModelDiscovery.GetPocoCloneTypes(members, config, cancellationToken)
            .Select(pocoType => SparseModelDiscovery.CreatePocoCloneModel(pocoType, config, cancellationToken))
            .ToImmutableArray();
        var structuralModels = SparseModelDiscovery.CollectStructuralTypes(members, config, cancellationToken)
            .Select(type => SparseModelDiscovery.CreateStructuralModel(type, config, cancellationToken))
            .ToImmutableArray();

        return new SparseGenerationAnalysis(
            SparseModelDiscovery.CreateModelInfo(model, hintName, cancellationToken),
            memberModels,
            pocoCloneModels,
            structuralModels,
            ImmutableArray<SparseGeneratorDiagnostic>.Empty
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
}
