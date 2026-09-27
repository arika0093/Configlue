using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Configlue.Generator;

[Generator(LanguageNames.CSharp)]
public sealed partial class ConfiglueGenerator : IIncrementalGenerator
{
    private const string ModelAttributeName = "Configlue.ConfiglueModelAttribute";
    private const string PreviousVersionAttributeName =
        "Configlue.ConfigluePreviousVersionAttribute";
    private const string MergeAttributeName = "Configlue.ConfiglueMergeAttribute";
    private const int InitialSchemaVersion = 1;
    private static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
        );

    private static readonly DiagnosticDescriptor MustBePartial = new(
        "CFG001",
        "Configlue model must be partial",
        "Model '{0}' must be declared partial",
        "Configlue",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor UnsupportedModel = new(
        "CFG002",
        "Unsupported Configlue model",
        "Model '{0}' must be a top-level, non-generic class or struct",
        "Configlue",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor MissingConstructor = new(
        "CFG003",
        "Model needs a public parameterless constructor",
        "Class model '{0}' must have a public parameterless constructor",
        "Configlue",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor UnsupportedRequired = new(
        "CFG004",
        "Required model members are unsupported",
        "Required member '{0}' cannot be omitted from a sparse fragment",
        "Configlue",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor UnsupportedMerge = new(
        "CFG005",
        "Unsupported merge mode",
        "Merge mode '{0}' is not supported for member '{1}'",
        "Configlue",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor InvalidPreviousVersion = new(
        "CFG006",
        "Invalid Configlue previous version",
        "Previous model '{0}' must declare a distinct lower version with the same schema ID as model '{1}'",
        "Configlue",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor InvalidModelId = new(
        "CFG007",
        "Invalid Configlue model schema ID",
        "Model '{0}' must declare a non-blank schema ID as the first ConfiglueModel attribute argument",
        "Configlue",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor InvalidModelVersion = new(
        "CFG008",
        "Invalid Configlue model schema version",
        "Model '{0}' must declare a schema version of 1 or greater",
        "Configlue",
        DiagnosticSeverity.Error,
        true
    );

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

    private static IEnumerable<MemberModel> GetMembers(
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
            if (merge?.ConstructorArguments.FirstOrDefault().Value is int requestedMode)
            {
                mode = requestedMode;
            }

            yield return new MemberModel(
                index++,
                property,
                child,
                mode,
                GetCollectionInfo(property.Type)
            );
        }
    }

    private static ImmutableArray<PreviousModelInfo> GetPreviousModels(
        INamedTypeSymbol model,
        string modelId,
        int modelVersion,
        CancellationToken cancellationToken,
        ImmutableArray<GeneratorDiagnosticInfo>.Builder diagnostics
    )
    {
        var previousModels = ImmutableArray.CreateBuilder<PreviousModelInfo>();
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
                new PreviousModelInfo(
                    previousModel,
                    GetMembers(previousModel, cancellationToken).ToImmutableArray()
                )
            );
        }

        return previousModels.ToImmutable();
    }

    private static bool IsConfiglueModel(ITypeSymbol type, CancellationToken cancellationToken)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class } named)
        {
            return false;
        }

        return HasConfiglueModelAttribute(named, cancellationToken);
    }

    private static bool HasConfiglueModelAttribute(
        INamedTypeSymbol model,
        CancellationToken cancellationToken
    )
    {
        foreach (var attribute in model.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass?.ToDisplayString() == ModelAttributeName)
            {
                return true;
            }
        }

        return false;
    }

    private static CollectionInfo GetCollectionInfo(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return new CollectionInfo(CollectionKind.Array, array.ElementType, null);
        }

        if (type is not INamedTypeSymbol named || named.TypeArguments.Length != 1)
        {
            return CollectionInfo.Unsupported;
        }

        var elementType = named.TypeArguments[0];
        var definition = named.ConstructedFrom.ToDisplayString();
        var kind = definition switch
        {
            "System.Collections.Generic.List<T>" => CollectionKind.List,
            "System.Collections.Generic.IEnumerable<T>"
            or "System.Collections.Generic.IReadOnlyCollection<T>"
            or "System.Collections.Generic.IReadOnlyList<T>" => CollectionKind.Array,
            "System.Collections.Generic.HashSet<T>"
            or "System.Collections.Generic.ISet<T>"
            or "System.Collections.Generic.IReadOnlySet<T>" => CollectionKind.Set,
            _ => CollectionKind.Unsupported,
        };

        return new CollectionInfo(kind, elementType, named);
    }

    private sealed class MemberModel(
        int id,
        IPropertySymbol property,
        INamedTypeSymbol? childModel,
        int mergeMode,
        CollectionInfo collection
    )
    {
        public int Id { get; } = id;
        public IPropertySymbol Property { get; } = property;
        public INamedTypeSymbol? ChildModel { get; } = childModel;
        public int MergeMode { get; } = mergeMode;
        public CollectionInfo Collection { get; } = collection;
    }

    private sealed class PreviousModelInfo(
        INamedTypeSymbol model,
        ImmutableArray<MemberModel> members
    )
    {
        public INamedTypeSymbol Model { get; } = model;
        public ImmutableArray<MemberModel> Members { get; } = members;
    }

    private sealed class CollectionInfo(
        CollectionKind kind,
        ITypeSymbol elementType,
        INamedTypeSymbol? namedType
    )
    {
        public CollectionKind Kind { get; } = kind;
        public ITypeSymbol ElementType { get; } = elementType;
        public INamedTypeSymbol? NamedType { get; } = namedType;
        public static CollectionInfo Unsupported { get; } =
            new(CollectionKind.Unsupported, null!, null);
    }

    private enum CollectionKind
    {
        Unsupported,
        Array,
        List,
        Set,
    }

    private sealed class GenerationResult : IEquatable<GenerationResult>
    {
        public GenerationResult(
            string? hintName,
            string? source,
            ImmutableArray<GeneratorDiagnosticInfo> diagnostics
        )
        {
            HintName = hintName;
            Source = source;
            Diagnostics = diagnostics;
        }

        public string? HintName { get; }
        public string? Source { get; }
        public ImmutableArray<GeneratorDiagnosticInfo> Diagnostics { get; }

        public bool Equals(GenerationResult? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (
                other is null
                || !string.Equals(HintName, other.HintName, StringComparison.Ordinal)
                || !string.Equals(Source, other.Source, StringComparison.Ordinal)
                || Diagnostics.Length != other.Diagnostics.Length
            )
            {
                return false;
            }

            for (var index = 0; index < Diagnostics.Length; index++)
            {
                if (!Diagnostics[index].Equals(other.Diagnostics[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is GenerationResult other && Equals(other);

        public override int GetHashCode()
        {
            var hash = unchecked(
                (HintName is null ? 0 : StringComparer.Ordinal.GetHashCode(HintName)) * 31
                + (Source is null ? 0 : StringComparer.Ordinal.GetHashCode(Source))
            );
            foreach (var diagnostic in Diagnostics)
            {
                hash = unchecked(hash * 31 + diagnostic.GetHashCode());
            }

            return hash;
        }
    }

    private readonly struct GeneratorDiagnosticInfo : IEquatable<GeneratorDiagnosticInfo>
    {
        private GeneratorDiagnosticInfo(
            DiagnosticDescriptor descriptor,
            GeneratorLocationInfo location,
            string? argument1,
            string? argument2
        )
        {
            Descriptor = descriptor;
            Location = location;
            Argument1 = argument1;
            Argument2 = argument2;
        }

        public DiagnosticDescriptor Descriptor { get; }
        public GeneratorLocationInfo Location { get; }
        public string? Argument1 { get; }
        public string? Argument2 { get; }

        public static GeneratorDiagnosticInfo Create(
            DiagnosticDescriptor descriptor,
            Location? location,
            string? argument1,
            string? argument2 = null
        )
        {
            return new GeneratorDiagnosticInfo(
                descriptor,
                GeneratorLocationInfo.Create(location),
                argument1,
                argument2
            );
        }

        public bool Equals(GeneratorDiagnosticInfo other)
        {
            return string.Equals(Descriptor.Id, other.Descriptor.Id, StringComparison.Ordinal)
                && Location.Equals(other.Location)
                && string.Equals(Argument1, other.Argument1, StringComparison.Ordinal)
                && string.Equals(Argument2, other.Argument2, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) =>
            obj is GeneratorDiagnosticInfo other && Equals(other);

        public override int GetHashCode()
        {
            var hash = StringComparer.Ordinal.GetHashCode(Descriptor.Id);
            hash = unchecked(hash * 31 + Location.GetHashCode());
            hash = unchecked(
                hash * 31 + (Argument1 is null ? 0 : StringComparer.Ordinal.GetHashCode(Argument1))
            );
            return unchecked(
                hash * 31 + (Argument2 is null ? 0 : StringComparer.Ordinal.GetHashCode(Argument2))
            );
        }
    }

    private readonly struct GeneratorLocationInfo : IEquatable<GeneratorLocationInfo>
    {
        public GeneratorLocationInfo(
            bool isSource,
            string? filePath,
            TextSpan span,
            LinePositionSpan lineSpan
        )
        {
            IsSource = isSource;
            FilePath = filePath;
            Span = span;
            LineSpan = lineSpan;
        }

        public bool IsSource { get; }
        public string? FilePath { get; }
        public TextSpan Span { get; }
        public LinePositionSpan LineSpan { get; }

        public static GeneratorLocationInfo Create(Location? location)
        {
            if (location is not { IsInSource: true })
            {
                return new GeneratorLocationInfo(false, null, default, default);
            }

            var lineSpan = location.GetLineSpan();
            return new GeneratorLocationInfo(
                true,
                location.SourceTree?.FilePath ?? lineSpan.Path ?? string.Empty,
                location.SourceSpan,
                lineSpan.Span
            );
        }

        public bool Equals(GeneratorLocationInfo other)
        {
            return IsSource == other.IsSource
                && string.Equals(FilePath, other.FilePath, StringComparison.Ordinal)
                && Span.Equals(other.Span)
                && LineSpan.Equals(other.LineSpan);
        }

        public override bool Equals(object? obj) =>
            obj is GeneratorLocationInfo other && Equals(other);

        public override int GetHashCode()
        {
            var hash = IsSource ? 1 : 0;
            hash = unchecked(
                hash * 31 + (FilePath is null ? 0 : StringComparer.Ordinal.GetHashCode(FilePath))
            );
            hash = unchecked(hash * 31 + Span.GetHashCode());
            return unchecked(hash * 31 + LineSpan.GetHashCode());
        }
    }
}
