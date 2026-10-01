using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
        var providerRegistries = context
            .CompilationProvider.Select(
                static (compilation, _) =>
                    (
                        Json: compilation.GetTypeByMetadataName(
                            "Configlue.Provider.Json.ConfiglueJsonFragmentRegistry`1"
                        )
                            is not null,
                        MessagePack: compilation.GetTypeByMetadataName(
                            "Configlue.Provider.MessagePack.ConfiglueMessagePackFragmentRegistry`1"
                        )
                            is not null
                    )
            )
            .WithComparer(EqualityComparer<(bool Json, bool MessagePack)>.Default);

        var analyzed = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                ModelAttributeName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (attributeContext, cancellationToken) =>
                    Analyze((INamedTypeSymbol)attributeContext.TargetSymbol, cancellationToken)
            )
            .WithComparer(EqualityComparer<GenerationAnalysis>.Default)
            .WithTrackingName("ConfiglueGenerator.Analysis");
        var generated = analyzed
            .Combine(providerRegistries)
            .Select(
                static (input, cancellationToken) =>
                    Render(input.Left, input.Right.Json, input.Right.MessagePack, cancellationToken)
            )
            .WithComparer(EqualityComparer<GenerationResult>.Default)
            .WithTrackingName("ConfiglueGenerator.Output");

        context.RegisterSourceOutput(
            generated,
            static (productionContext, result) => Emit(productionContext, result)
        );

        var hasModels = analyzed
            .Select(static (analysis, _) => analysis.Model.HasValue)
            .Collect()
            .Select(static (presence, _) => presence.Any(static value => value))
            .WithComparer(EqualityComparer<bool>.Default);
        var shouldEmitIsExternalInit = context
            .CompilationProvider.Combine(context.AnalyzerConfigOptionsProvider)
            .Combine(hasModels)
            .Select(
                static (input, _) =>
                {
                    var ((compilation, options), anyModels) = input;
                    return anyModels
                        && ShouldEmitIsExternalInit(
                            compilation,
                            IsExternalInitEmissionEnabled(options)
                        );
                }
            )
            .WithComparer(EqualityComparer<bool>.Default)
            .WithTrackingName("ConfiglueGenerator.IsExternalInit");
        context.RegisterSourceOutput(
            shouldEmitIsExternalInit,
            static (productionContext, emit) =>
            {
                if (!emit)
                {
                    return;
                }

                productionContext.AddSource(
                    IsExternalInitHintName,
                    SourceText.From(IsExternalInitSource, Encoding.UTF8)
                );
            }
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

    private static GenerationResult Render(
        GenerationAnalysis analysis,
        bool hasJsonFragmentRegistry,
        bool hasMessagePackFragmentRegistry,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!analysis.Model.HasValue)
        {
            return new GenerationResult(null, null, analysis.Diagnostics);
        }

        var model = analysis.Model.Value;
        var source = BuildSource(
            model,
            analysis.Members,
            analysis.PreviousModels,
            analysis.PocoCloneModels,
            analysis.StructuralModels,
            hasJsonFragmentRegistry,
            hasMessagePackFragmentRegistry,
            cancellationToken
        );
        return new GenerationResult(analysis.HintName, source, analysis.Diagnostics);
    }

    private static GenerationAnalysis Analyze(
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
            return AnalysisFailure(MustBePartial, location, model.Name);
        }

        if (
            model.ContainingType is not null
            || model.Arity != 0
            || (model.TypeKind != TypeKind.Class && model.TypeKind != TypeKind.Struct)
        )
        {
            return AnalysisFailure(UnsupportedModel, location, model.Name);
        }

        if (model.IsAbstract)
        {
            return AnalysisFailure(UnsupportedModel, location, model.Name);
        }

        if (
            model.TypeKind == TypeKind.Class
            && !HasPublicParameterlessConstructor(model, cancellationToken)
        )
        {
            return AnalysisFailure(MissingConstructor, location, model.Name);
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
                : ImmutableArray<SymbolPreviousModelInfo>.Empty;
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

            if (SparseFragments.Generator.Shared.RoslynSymbolCompat.IsRequired(member.Property))
            {
                diagnostics.Add(
                    GeneratorDiagnosticInfo.Create(
                        UnsupportedRequired,
                        member.Property.Locations.FirstOrDefault(),
                        member.Property.Name
                    )
                );
            }

            var unsupportedReason =
                SparseFragments.Generator.Shared.SparseMergeValidation.GetUnsupportedReason(
                    member.MergeMode,
                    member.ChildModel is not null,
                    member.Collection.Kind
                );
            if (unsupportedReason is not null)
            {
                diagnostics.Add(
                    GeneratorDiagnosticInfo.Create(
                        UnsupportedMerge,
                        member.Property.Locations.FirstOrDefault(),
                        unsupportedReason,
                        member.Property.Name
                    )
                );
            }
        }

        if (diagnostics.Count > 0)
        {
            return new GenerationAnalysis(
                null,
                null,
                ImmutableArray<MemberModel>.Empty,
                ImmutableArray<PreviousModelInfo>.Empty,
                ImmutableArray<PocoCloneModel>.Empty,
                ImmutableArray<StructuralModel>.Empty,
                diagnostics.ToImmutable()
            );
        }

        var fullyQualifiedName = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var fileName =
            Sanitize(fullyQualifiedName, cancellationToken)
            + "_"
            + GetStableTypeHash(fullyQualifiedName, cancellationToken)
            + ".Configlue.g.cs";
        var memberModels = CreateMemberModels(members, cancellationToken);
        var previousModelInfos = CreatePreviousModelInfos(
            previousModels,
            members,
            memberModels,
            cancellationToken
        );
        var pocoCloneModels = GetPocoCloneTypes(members, cancellationToken)
            .Select(pocoType => CreatePocoCloneModel(pocoType, cancellationToken))
            .ToImmutableArray();
        var structuralModels = CollectStructuralTypes(members, cancellationToken)
            .Select(type => CreateStructuralModel(type, cancellationToken))
            .ToImmutableArray();
        return new GenerationAnalysis(
            fileName,
            CreateModelInfo(model, modelId, modelVersion, cancellationToken),
            memberModels,
            previousModelInfos,
            pocoCloneModels,
            structuralModels,
            ImmutableArray<GeneratorDiagnosticInfo>.Empty
        );
    }

    private static ImmutableArray<INamedTypeSymbol> CollectStructuralTypes(
        ImmutableArray<SymbolMemberModel> members,
        CancellationToken cancellationToken
    )
    {
        var result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<INamedTypeSymbol>();
        foreach (var child in members.Select(static member => member.ChildModel))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child is not null && IsStructuralType(child, cancellationToken))
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
                var nested in GetMembers(type, cancellationToken)
                    .Select(static member => member.ChildModel)
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (nested is not null && IsStructuralType(nested, cancellationToken))
                {
                    pending.Push(nested);
                }
            }
        }

        return result.ToImmutable();
    }

    private static StructuralModel CreateStructuralModel(
        INamedTypeSymbol type,
        CancellationToken cancellationToken
    )
    {
        return new StructuralModel(
            StructuralHostName(type, cancellationToken),
            NonNullableTypeName(type),
            CreateMemberModels(
                GetMembers(type, cancellationToken).ToImmutableArray(),
                cancellationToken
            )
        );
    }

    private static GenerationAnalysis AnalysisFailure(
        DiagnosticDescriptor descriptor,
        Location? location,
        string? argument1
    )
    {
        return new GenerationAnalysis(
            null,
            null,
            ImmutableArray<MemberModel>.Empty,
            ImmutableArray<PreviousModelInfo>.Empty,
            ImmutableArray<PocoCloneModel>.Empty,
            ImmutableArray<StructuralModel>.Empty,
            ImmutableArray.Create(GeneratorDiagnosticInfo.Create(descriptor, location, argument1))
        );
    }
}
