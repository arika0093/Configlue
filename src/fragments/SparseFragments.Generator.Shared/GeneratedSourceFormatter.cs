using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SparseFragments.Generator.Shared;

internal static class GeneratedSourceFormatter
{
    // Syntax formatting handles braces inside literals, comments and initializers correctly.
    public static string Format(string source, CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return root.NormalizeWhitespace(indentation: "    ", eol: "\n").ToFullString() + "\n";
    }
}
