using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SparseFragments.Generator.Shared;

/// <summary>Shared identifier and type-name formatting used by both generators.</summary>
internal static class SparseNaming
{
    public const int InitialSchemaVersion = 1;
    public const int CustomMergeMode = 4;

    public static bool IsCoreGeneratedName(string name) =>
        name
            is "Fragment"
                or "FragmentBuilder"
                or "Empty"
                or "IsEmpty"
                or "Merge"
                or "ApplyChanges"
                or "Diff"
                or "DeepClone"
                or "From"
                or "ToModel"
                or "ToBuilder"
                or "Build"
        || name.StartsWith("__", System.StringComparison.Ordinal);

    public static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
        );

    public static string TypeName(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    public static string NonNullableTypeName(ITypeSymbol type) =>
        type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(TypeFormat);

    public static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None
        || SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None
            ? "@" + identifier
            : identifier;

    public static string PatchApiPrefix(IEnumerable<string> memberNames)
    {
        var names = new HashSet<string>(memberNames);
        var prefix = new StringBuilder();
        while (
            new[] { "Between", "Compose", "Invert", "Rebase" }.Any(name =>
                names.Contains(prefix.ToString() + name)
            )
        )
            prefix.Append("Sparse");
        return prefix.ToString();
    }

    public static string JsonPatchApiPrefix(IEnumerable<string> memberNames)
    {
        var names = new HashSet<string>(memberNames);
        var prefix = new StringBuilder();
        while (
            new[] { "FromJsonPatch", "ToJsonPatch" }.Any(name =>
                names.Contains(prefix.ToString() + name)
            )
        )
            prefix.Append("Sparse");
        return prefix.ToString();
    }

    public static string Sanitize(string identifier, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(identifier.Length);
        foreach (var character in identifier)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    public static string GetStableTypeHash(string value, CancellationToken cancellationToken)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var character in value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash = unchecked((hash ^ character) * prime);
        }

        return hash.ToString("X8", CultureInfo.InvariantCulture);
    }

    public static string MergeModeName(int mode) =>
        mode switch
        {
            1 => "Deep",
            2 => "Append",
            3 => "SetUnion",
            4 => "Custom",
            _ => "Replace",
        };
}
