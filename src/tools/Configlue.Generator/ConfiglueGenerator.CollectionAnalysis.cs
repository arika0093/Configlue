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
    private static bool IsValidMergeStrategy(
        INamedTypeSymbol strategyType,
        ITypeSymbol memberType,
        bool isNestedModel,
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
                current.OriginalDefinition.ToDisplayString()
                    == "Configlue.ConfiglueMergeStrategy<T>"
                && SymbolEqualityComparer.Default.Equals(current.TypeArguments[0], memberType)
            )
            {
                return true;
            }
        }

        return false;
    }

    private sealed class SymbolMemberModel(
        int id,
        IPropertySymbol property,
        INamedTypeSymbol? childModel,
        int mergeMode,
        SymbolCollectionInfo collection,
        INamedTypeSymbol? mergeStrategyType
    )
    {
        public int Id { get; } = id;
        public IPropertySymbol Property { get; } = property;
        public INamedTypeSymbol? ChildModel { get; } = childModel;
        public int MergeMode { get; } = mergeMode;
        public SymbolCollectionInfo Collection { get; } = collection;
        public INamedTypeSymbol? MergeStrategyType { get; } = mergeStrategyType;
    }

    private sealed class SymbolPreviousModelInfo(
        INamedTypeSymbol model,
        ImmutableArray<SymbolMemberModel> members
    )
    {
        public INamedTypeSymbol Model { get; } = model;
        public ImmutableArray<SymbolMemberModel> Members { get; } = members;
    }

    private sealed class GenerationResult : IEquatable<GenerationResult>
    {
        public GenerationResult(
            string? hintName,
            string? source,
            string? sparseHintName,
            string? sparseSource,
            ImmutableArray<GeneratorDiagnosticInfo> diagnostics
        )
        {
            HintName = hintName;
            Source = source;
            SparseHintName = sparseHintName;
            SparseSource = sparseSource;
            Diagnostics = diagnostics;
        }

        public string? HintName { get; }
        public string? Source { get; }
        public string? SparseHintName { get; }
        public string? SparseSource { get; }
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
                || !string.Equals(SparseHintName, other.SparseHintName, StringComparison.Ordinal)
                || !string.Equals(SparseSource, other.SparseSource, StringComparison.Ordinal)
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
            hash = unchecked(
                hash * 31
                + (SparseHintName is null
                    ? 0
                    : StringComparer.Ordinal.GetHashCode(SparseHintName))
            );
            hash = unchecked(
                hash * 31
                + (SparseSource is null ? 0 : StringComparer.Ordinal.GetHashCode(SparseSource))
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
