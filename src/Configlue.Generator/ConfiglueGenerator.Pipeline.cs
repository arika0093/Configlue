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
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var generated = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                ModelAttributeName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (attributeContext, cancellationToken) =>
                    Generate((INamedTypeSymbol)attributeContext.TargetSymbol, cancellationToken)
            )
            .WithComparer(EqualityComparer<GenerationResult>.Default);

        context.RegisterSourceOutput(
            generated,
            static (productionContext, result) => Emit(productionContext, result)
        );
    }

    private static void Emit(SourceProductionContext context, GenerationResult result)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        foreach (var diagnostic in result.Diagnostics)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var location = diagnostic.Location.IsSource
                ? Location.Create(
                    diagnostic.Location.FilePath!,
                    diagnostic.Location.Span,
                    diagnostic.Location.LineSpan
                )
                : Location.None;
            var roslynDiagnostic = diagnostic.Argument2 is null
                ? Diagnostic.Create(diagnostic.Descriptor, location, diagnostic.Argument1)
                : Diagnostic.Create(
                    diagnostic.Descriptor,
                    location,
                    diagnostic.Argument1,
                    diagnostic.Argument2
                );
            context.ReportDiagnostic(roslynDiagnostic);
        }

        if (result.HintName is not null && result.Source is not null)
        {
            context.AddSource(result.HintName, SourceText.From(result.Source, Encoding.UTF8));
        }
    }

    private static GenerationResult Generate(
        INamedTypeSymbol model,
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
            return Failure(MustBePartial, location, model.Name);
        }

        if (
            model.ContainingType is not null
            || model.Arity != 0
            || (model.TypeKind != TypeKind.Class && model.TypeKind != TypeKind.Struct)
        )
        {
            return Failure(UnsupportedModel, location, model.Name);
        }

        if (model.IsAbstract)
        {
            return Failure(UnsupportedModel, location, model.Name);
        }

        if (
            model.TypeKind == TypeKind.Class
            && !HasPublicParameterlessConstructor(model, cancellationToken)
        )
        {
            return Failure(MissingConstructor, location, model.Name);
        }

        var members = GetMembers(model, cancellationToken).ToImmutableArray();
        var diagnostics = ImmutableArray.CreateBuilder<GeneratorDiagnosticInfo>();
        var modelId = GetModelId(model, cancellationToken);
        var modelVersion = GetModelVersion(model, cancellationToken);
        var modelIdValid = !string.IsNullOrWhiteSpace(modelId);
        var modelVersionValid = modelVersion >= InitialSchemaVersion;
        if (!modelIdValid)
        {
            diagnostics.Add(GeneratorDiagnosticInfo.Create(InvalidModelId, location, model.Name));
        }

        if (!modelVersionValid)
        {
            diagnostics.Add(
                GeneratorDiagnosticInfo.Create(InvalidModelVersion, location, model.Name)
            );
        }

        var previousModels =
            modelIdValid && modelVersionValid
                ? GetPreviousModels(model, modelId, modelVersion, cancellationToken, diagnostics)
                : ImmutableArray<PreviousModelInfo>.Empty;
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
                        cancellationToken
                    )
                )
            )
            {
                diagnostics.Add(
                    GeneratorDiagnosticInfo.Create(
                        InvalidMergeStrategy,
                        member.Property.Locations.FirstOrDefault(),
                        member.MergeStrategyType?.ToDisplayString() ?? "<missing>",
                        member.Property.Name
                    )
                );
            }

            if (member.Property.IsRequired)
            {
                diagnostics.Add(
                    GeneratorDiagnosticInfo.Create(
                        UnsupportedRequired,
                        member.Property.Locations.FirstOrDefault(),
                        member.Property.Name
                    )
                );
            }

            if (member.MergeMode is < 0 or > CustomMergeMode)
            {
                diagnostics.Add(
                    GeneratorDiagnosticInfo.Create(
                        UnsupportedMerge,
                        member.Property.Locations.FirstOrDefault(),
                        member.MergeMode.ToString(),
                        member.Property.Name
                    )
                );
            }

            if (member.MergeMode == 1 && member.ChildModel is null)
            {
                diagnostics.Add(
                    GeneratorDiagnosticInfo.Create(
                        UnsupportedMerge,
                        member.Property.Locations.FirstOrDefault(),
                        "Deep",
                        member.Property.Name
                    )
                );
            }

            if (
                (member.MergeMode == 2 || member.MergeMode == 3)
                && member.Collection.Kind == CollectionKind.Unsupported
            )
            {
                diagnostics.Add(
                    GeneratorDiagnosticInfo.Create(
                        UnsupportedMerge,
                        member.Property.Locations.FirstOrDefault(),
                        member.MergeMode == 2 ? "Append" : "SetUnion",
                        member.Property.Name
                    )
                );
            }

            if (member.MergeMode == 2 && member.Collection.Kind == CollectionKind.Set)
            {
                diagnostics.Add(
                    GeneratorDiagnosticInfo.Create(
                        UnsupportedMerge,
                        member.Property.Locations.FirstOrDefault(),
                        "Append on set types (use an ordered collection or SetUnion)",
                        member.Property.Name
                    )
                );
            }
        }

        if (diagnostics.Count > 0)
        {
            return new GenerationResult(null, null, diagnostics.ToImmutable());
        }

        var source = BuildSource(model, members, previousModels, cancellationToken);
        var fullyQualifiedName = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var fileName =
            Sanitize(fullyQualifiedName, cancellationToken)
            + "_"
            + GetStableTypeHash(fullyQualifiedName, cancellationToken)
            + ".Configlue.g.cs";
        return new GenerationResult(
            fileName,
            source,
            ImmutableArray<GeneratorDiagnosticInfo>.Empty
        );
    }

    private static GenerationResult Failure(
        DiagnosticDescriptor descriptor,
        Location? location,
        string? argument1
    )
    {
        return new GenerationResult(
            null,
            null,
            ImmutableArray.Create(GeneratorDiagnosticInfo.Create(descriptor, location, argument1))
        );
    }
}
