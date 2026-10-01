using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SparseFragments.Generator.Shared;

internal static class RoslynSymbolCompat
{
    private const string RequiredMemberAttributeName =
        "System.Runtime.CompilerServices.RequiredMemberAttribute";

    public static bool IsRequired(IPropertySymbol property)
    {
        ArgumentNullException.ThrowIfNull(property);
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
