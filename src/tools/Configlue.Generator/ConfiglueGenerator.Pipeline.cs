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
                            is not null,
                        BclSetSupportsReadOnlySet: SparseFragments.Generator.Shared.SparseCollectionAnalyzer.HashSetImplementsReadOnlySet(
                            compilation
                        )
                    )
            )
            .WithComparer(
                EqualityComparer<(
                    bool Json,
                    bool MessagePack,
                    bool BclSetSupportsReadOnlySet
                )>.Default
            );

        var analyzed = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                ModelAttributeName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (attributeContext, cancellationToken) =>
                    Analyze((INamedTypeSymbol)attributeContext.TargetSymbol, cancellationToken)
            )
            .WithComparer(EqualityComparer<GenerationAnalysis>.Default)
            .WithTrackingName("ConfiglueGenerator.Analysis");
        var duplicateModelIdentities = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                ModelAttributeName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (attributeContext, cancellationToken) =>
                    GetModelIdentity(
                        (INamedTypeSymbol)attributeContext.TargetSymbol,
                        cancellationToken
                    )
            )
            .WithComparer(EqualityComparer<ModelIdentity>.Default)
            .Collect()
            .Select(static (identities, _) => FindDuplicateModelIdentities(identities))
            .WithTrackingName("ConfiglueGenerator.ModelIdentity");
        context.RegisterSourceOutput(
            duplicateModelIdentities,
            static (productionContext, diagnostics) =>
                ReportDiagnostics(productionContext, diagnostics)
        );
        var generated = analyzed
            .Combine(providerRegistries)
            .Select(
                static (input, cancellationToken) =>
                    Render(
                        input.Left,
                        input.Right.Json,
                        input.Right.MessagePack,
                        input.Right.BclSetSupportsReadOnlySet,
                        cancellationToken
                    )
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
        ReportDiagnostics(context, result.Diagnostics);

        if (result.HintName is not null && result.Source is not null)
        {
            context.AddSource(result.HintName, SourceText.From(result.Source, Encoding.UTF8));
        }
    }

    private static void ReportDiagnostics(
        SourceProductionContext context,
        ImmutableArray<GeneratorDiagnosticInfo> diagnostics
    )
    {
        foreach (var diagnostic in diagnostics)
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
    }

    private static ModelIdentity GetModelIdentity(
        INamedTypeSymbol model,
        CancellationToken cancellationToken
    ) =>
        new(
            model.Name,
            GetModelId(model, cancellationToken),
            GetModelVersion(model, cancellationToken),
            GeneratorLocationInfo.Create(model.Locations.FirstOrDefault())
        );

    private static ImmutableArray<GeneratorDiagnosticInfo> FindDuplicateModelIdentities(
        ImmutableArray<ModelIdentity> identities
    )
    {
        var groups = new Dictionary<(string Id, int Version), List<ModelIdentity>>();
        foreach (var identity in identities)
        {
            if (string.IsNullOrWhiteSpace(identity.Id) || identity.Version < InitialSchemaVersion)
            {
                continue;
            }

            var key = (identity.Id, identity.Version);
            if (!groups.TryGetValue(key, out var models))
            {
                models = new List<ModelIdentity>();
                groups.Add(key, models);
            }
            models.Add(identity);
        }

        var diagnostics = ImmutableArray.CreateBuilder<GeneratorDiagnosticInfo>();
        foreach (var models in groups.Values)
        {
            if (models.Count < 2)
            {
                continue;
            }

            for (var index = 0; index < models.Count; index++)
            {
                var counterpart = index == 0 ? models[1] : models[0];
                diagnostics.Add(
                    GeneratorDiagnosticInfo.Create(
                        DuplicateModelIdentity,
                        models[index].Location,
                        models[index].Name,
                        counterpart.Name
                    )
                );
            }
        }

        return diagnostics.ToImmutable();
    }

    private static GenerationResult Render(
        GenerationAnalysis analysis,
        bool hasJsonFragmentRegistry,
        bool hasMessagePackFragmentRegistry,
        bool bclSetSupportsReadOnlySet,
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
            bclSetSupportsReadOnlySet,
            cancellationToken
        );
        return new GenerationResult(analysis.HintName, source, analysis.Diagnostics);
    }

    private static GenerationAnalysis Analyze(
        INamedTypeSymbol model,
        CancellationToken cancellationToken,
        bool validateDependencies = true
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

        if (!IsSupportedRootModelShape(model, declaration))
        {
            return AnalysisFailure(UnsupportedModel, location, model.Name);
        }

        if (
            model.TypeKind == TypeKind.Class
            && SparseFragments.Generator.Shared.ModelConstructorBinding.AnalyzeRoot(
                model,
                cancellationToken
            )
                is null
        )
        {
            return AnalysisFailure(MissingConstructor, location, model.Name);
        }

        var members = GetMembers(model, cancellationToken).ToImmutableArray();
        var rootCollision = model
            .GetMembers()
            .FirstOrDefault(static symbol => IsGeneratedRootName(symbol.Name));
        if (rootCollision is not null)
        {
            return AnalysisFailure(
                GeneratedNameCollision,
                rootCollision.Locations.FirstOrDefault(),
                rootCollision.Name
            );
        }
        if (IsGeneratedRootName(model.Name))
        {
            return AnalysisFailure(GeneratedNameCollision, location, model.Name);
        }
        var extensionCollision = model
            .ContainingNamespace.GetTypeMembers(model.Name + "PatchOptionsExtensions")
            .Concat(model.ContainingNamespace.GetTypeMembers(model.Name + "DetailsExtensions"))
            .FirstOrDefault(static type => type.Arity == 0);
        if (extensionCollision is not null)
            return AnalysisFailure(
                GeneratedNameCollision,
                extensionCollision.Locations.FirstOrDefault(),
                extensionCollision.Name
            );
        var generatedNameCollision = members.FirstOrDefault(static member =>
            IsGeneratedNameCollision(member.Property.Name)
        );
        if (generatedNameCollision is not null)
        {
            return AnalysisFailure(
                GeneratedNameCollision,
                generatedNameCollision.Property.Locations.FirstOrDefault(),
                generatedNameCollision.Property.Name
            );
        }

        var diagnostics = ImmutableArray.CreateBuilder<GeneratorDiagnosticInfo>();
        foreach (var property in members.Select(static member => member.Property))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                property.ReturnsByRef
                || property.ReturnsByRefReadonly
                || !IsSupportedGeneratedMemberType(property.Type, cancellationToken)
            )
            {
                diagnostics.Add(
                    GeneratorDiagnosticInfo.Create(
                        UnsupportedMemberType,
                        property.Locations.FirstOrDefault(),
                        property.Name,
                        property.Type.ToDisplayString()
                    )
                );
            }
        }

        foreach (
            var structuralType in CollectStructuralTypes(members, cancellationToken).Prepend(model)
        )
        {
            var scopeMembers = GetMembers(structuralType, cancellationToken).ToImmutableArray();
            foreach (
                var property in scopeMembers
                    .Where(member =>
                        IsGeneratedNameCollision(member.Property.Name)
                        || scopeMembers.Any(child =>
                            child.ChildModel is not null
                            && child.Property.SetMethod is { IsInitOnly: false }
                            && member.Property.Name == "Set" + child.Property.Name
                        )
                    )
                    .Select(static member => member.Property)
            )
            {
                diagnostics.Add(
                    GeneratorDiagnosticInfo.Create(
                        GeneratedNameCollision,
                        property.Locations.FirstOrDefault(),
                        property.Name
                    )
                );
            }
            var jsonNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in scopeMembers.Select(static member => member.Property))
            {
                var wireName = GetJsonPropertyName(property, cancellationToken, out _);
                if (jsonNames.TryGetValue(wireName, out var other))
                {
                    diagnostics.Add(
                        GeneratorDiagnosticInfo.Create(
                            DuplicateJsonPropertyName,
                            property.Locations.FirstOrDefault(),
                            property.Name,
                            other
                        )
                    );
                }
                else
                {
                    jsonNames.Add(wireName, property.Name);
                }
            }
        }
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
        foreach (
            var member in SparseFragments.Generator.Shared.ModelConstructionPlan.UnsupportedRequiredMembers(
                model,
                members.Select(static member => member.Property),
                cancellationToken
            )
        )
            diagnostics.Add(
                GeneratorDiagnosticInfo.Create(
                    UnsupportedRequired,
                    member.Locations.FirstOrDefault(),
                    member.Name
                )
            );

        foreach (
            var property in SparseFragments.Generator.Shared.SparseModelAnalyzer.UnsupportedStructuralMembers(
                model,
                SparseConfiguration,
                cancellationToken
            )
        )
            diagnostics.Add(
                GeneratorDiagnosticInfo.Create(
                    UnsupportedStructural,
                    property.Locations.FirstOrDefault(),
                    property.Name
                )
            );

        foreach (
            var property in SparseFragments.Generator.Shared.SparseModelAnalyzer.UnsupportedCloneMembers(
                model,
                SparseConfiguration,
                cancellationToken
            )
        )
            diagnostics.Add(
                GeneratorDiagnosticInfo.Create(
                    UnsupportedClone,
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

        if (diagnostics.Count == 0 && validateDependencies)
        {
            AddDependentModelDiagnostics(model, members, cancellationToken, diagnostics);
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

    private static bool IsGeneratedRootName(string name) =>
        name
            is "Fragment"
                or "Patch"
                or "Details"
                or "Observable"
                or "FragmentBuilder"
                or "ConfiglueSchema"
                or "FragmentSchema"
                or "DeepClone"
        || name.StartsWith("__", StringComparison.Ordinal);

    private static bool IsGeneratedNameCollision(string memberName) =>
        SparseFragments.Generator.Shared.SparseNaming.IsCoreGeneratedName(memberName)
        || memberName
            is "Patch"
                or "Details"
                or "Observable"
                or "ConfiglueSchema"
                or "Schema"
                or "ToPatch"
                or "FragmentSchema"
                or "FromPrevious"
                or "Apply"
                or "EnumeratePresentMembers"
                or "WithMember"
                or "WithoutMember"
                or "JsonConverter"
                or "MessagePackFormatter"
                or "FragmentJsonConverter"
                or "FragmentMessagePackFormatter"
                or "PropertyChanged"
                or "Leaf"
                or "Collection"
                or "ToReadOnly"
                or "MapStatus"
                or "ApplyNested"
                or "ClonePatch"
                or "WithUnspecifiedMembersUnset"
                or "SelectMembers"
                or "Route"
                or "RouteCore"
                or "MergePatch"
                or "MergeRoutedPatch";

    private static void AddDependentModelDiagnostics(
        INamedTypeSymbol root,
        ImmutableArray<SymbolMemberModel> members,
        CancellationToken cancellationToken,
        ImmutableArray<GeneratorDiagnosticInfo>.Builder diagnostics
    )
    {
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default) { root };
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member.ChildModel is { } child)
            {
                ValidateDependentModel(
                    child,
                    member.Property.Locations.FirstOrDefault(),
                    visited,
                    cancellationToken,
                    diagnostics
                );
            }
        }

        foreach (var attribute in root.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass?.ToDisplayString() != PreviousVersionAttributeName)
            {
                continue;
            }

            if (attribute.ConstructorArguments.FirstOrDefault().Value is INamedTypeSymbol previous)
            {
                ValidateDependentModel(
                    previous,
                    attribute
                        .ApplicationSyntaxReference?.GetSyntax(cancellationToken)
                        .GetLocation(),
                    visited,
                    cancellationToken,
                    diagnostics
                );
            }
        }
    }

    private static void ValidateDependentModel(
        INamedTypeSymbol dependency,
        Location? referenceLocation,
        HashSet<INamedTypeSymbol> visited,
        CancellationToken cancellationToken,
        ImmutableArray<GeneratorDiagnosticInfo>.Builder diagnostics
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (
            !dependency
                .GetAttributes()
                .Any(attribute => attribute.AttributeClass?.ToDisplayString() == ModelAttributeName)
        )
        {
            return;
        }

        if (!visited.Add(dependency))
        {
            return;
        }

        var analysis = Analyze(dependency, cancellationToken, validateDependencies: false);
        if (!analysis.Model.HasValue || analysis.Diagnostics.Length > 0)
        {
            diagnostics.Add(
                GeneratorDiagnosticInfo.Create(
                    InvalidDependentModel,
                    referenceLocation,
                    dependency.Name
                )
            );
            return;
        }

        foreach (var member in GetMembers(dependency, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member.ChildModel is { } child)
            {
                ValidateDependentModel(
                    child,
                    member.Property.Locations.FirstOrDefault(),
                    visited,
                    cancellationToken,
                    diagnostics
                );
            }
        }

        foreach (var attribute in dependency.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                attribute.AttributeClass?.ToDisplayString() == PreviousVersionAttributeName
                && attribute.ConstructorArguments.FirstOrDefault().Value
                    is INamedTypeSymbol previous
            )
            {
                ValidateDependentModel(
                    previous,
                    attribute
                        .ApplicationSyntaxReference?.GetSyntax(cancellationToken)
                        .GetLocation(),
                    visited,
                    cancellationToken,
                    diagnostics
                );
            }
        }
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
            ),
            SparseFragments.Generator.Shared.ModelConstructorBinding.AnalyzeStructural(
                type,
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
