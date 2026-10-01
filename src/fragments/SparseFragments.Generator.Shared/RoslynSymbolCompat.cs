using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparseFragments.Generator.Shared;

internal static class RoslynSymbolCompat
{
    private const string RequiredMemberAttributeName =
        "System.Runtime.CompilerServices.RequiredMemberAttribute";

    public static bool IsRequired(ISymbol property)
    {
        ArgumentNullException.ThrowIfNull(property);
        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            var declaration = reference.GetSyntax();
            if (
                declaration is VariableDeclaratorSyntax
                && declaration.Parent?.Parent is FieldDeclarationSyntax field
            )
                declaration = field;
            if (
                declaration is PropertyDeclarationSyntax syntax
                && syntax.Modifiers.Any(static token => token.ValueText == "required")
            )
                return true;
            if (
                declaration is FieldDeclarationSyntax fieldSyntax
                && fieldSyntax.Modifiers.Any(static token => token.ValueText == "required")
            )
                return true;
        }
        return property
            .GetAttributes()
            .Any(static attribute =>
                string.Equals(
                    attribute.AttributeClass?.ToDisplayString(),
                    RequiredMemberAttributeName,
                    StringComparison.Ordinal
                )
            );
    }
}
