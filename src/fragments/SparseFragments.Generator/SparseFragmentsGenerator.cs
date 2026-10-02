using System.Collections.Immutable;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SparseFragments.Generator.Shared;

namespace SparseFragments.Generator;

/// <summary>Generates standalone sparse fragments for <c>[SparseFragmentModel]</c> types.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class SparseFragmentsGenerator : IIncrementalGenerator
{
    private const string ModelAttributeName = "SparseFragments.SparseFragmentModelAttribute";
    private const string MergeAttributeName = "SparseFragments.SparseMergeAttribute";
    private const string MergeStrategyBaseName = "SparseFragments.FragmentMergeStrategy<T>";

    private static readonly SparseGeneratorConfig Configuration = new(
        ModelAttributeName,
        MergeAttributeName,
        MergeStrategyBaseName
    );

    private static readonly DiagnosticDescriptor MustBePartial = new(
        SparseDiagnosticIds.MustBePartial,
        "Sparse fragment model must be partial",
        "Model '{0}' must be declared partial",
        "SparseFragments",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor UnsupportedModel = new(
        SparseDiagnosticIds.UnsupportedModel,
        "Unsupported sparse fragment model",
        "Model '{0}' must be a top-level, non-generic, non-abstract class or struct",
        "SparseFragments",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor MissingConstructor = new(
        SparseDiagnosticIds.MissingConstructor,
        "Model needs a supported constructor",
        "Class model '{0}' must have a parameterless constructor or a constructor whose parameters match public readable properties by name and type; a setter, when present, must be public",
        "SparseFragments",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor InvalidMergeStrategy = new(
        SparseDiagnosticIds.InvalidMergeStrategy,
        "Invalid custom merge strategy",
        "Merge strategy for member '{0}' must derive from FragmentMergeStrategy<TMember> and be a concrete, accessible type",
        "SparseFragments",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor UnsupportedMerge = new(
        SparseDiagnosticIds.UnsupportedMerge,
        "Unsupported merge mode",
        "The configured merge mode is not supported for member '{0}'",
        "SparseFragments",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor UnsupportedRequired = new(
        SparseDiagnosticIds.UnsupportedRequired,
        "Required member cannot be constructed",
        "Required member '{0}' must be represented by an accessible public property in the fragment construction plan",
        "SparseFragments",
        DiagnosticSeverity.Error,
        true
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var analyzed = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                ModelAttributeName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (attributeContext, cancellationToken) =>
                    SparseModelAnalyzer.Analyze(
                        (INamedTypeSymbol)attributeContext.TargetSymbol,
                        Configuration,
                        cancellationToken
                    )
            )
            .WithTrackingName("SparseFragmentsGenerator.Analysis");
        var generated = analyzed
            .Select(static (analysis, cancellationToken) => Render(analysis, cancellationToken))
            .WithTrackingName("SparseFragmentsGenerator.Output");

        context.RegisterSourceOutput(
            generated,
            static (productionContext, result) => Emit(productionContext, result)
        );
    }

    private static void Emit(SourceProductionContext context, SparseGenerationResult result)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        foreach (var diagnostic in result.Diagnostics)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.ReportDiagnostic(
                Diagnostic.Create(
                    GetDescriptor(diagnostic.DescriptorId),
                    diagnostic.Location ?? Location.None,
                    diagnostic.Argument1
                )
            );
        }

        if (result.HintName is not null && result.Source is not null)
        {
            context.AddSource(result.HintName, SourceText.From(result.Source, Encoding.UTF8));
        }
    }

    private static SparseGenerationResult Render(
        SparseGenerationAnalysis analysis,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!analysis.Model.HasValue)
        {
            return new SparseGenerationResult(null, null, analysis.Diagnostics);
        }

        var model = analysis.Model.Value;
        var source = SparseFragmentEmitter.BuildSource(
            model,
            analysis.Members,
            analysis.PocoCloneModels,
            analysis.StructuralModels,
            cancellationToken
        );
        return new SparseGenerationResult(model.HintName, source, analysis.Diagnostics);
    }

    private static DiagnosticDescriptor GetDescriptor(string id) =>
        id switch
        {
            SparseDiagnosticIds.MustBePartial => MustBePartial,
            SparseDiagnosticIds.UnsupportedModel => UnsupportedModel,
            SparseDiagnosticIds.MissingConstructor => MissingConstructor,
            SparseDiagnosticIds.InvalidMergeStrategy => InvalidMergeStrategy,
            SparseDiagnosticIds.UnsupportedMerge => UnsupportedMerge,
            SparseDiagnosticIds.UnsupportedRequired => UnsupportedRequired,
            _ => throw new global::System.ArgumentOutOfRangeException(nameof(id), id, null),
        };
}
