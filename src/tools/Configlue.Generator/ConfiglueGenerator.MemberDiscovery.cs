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
    private static IEnumerable<SymbolMemberModel> GetMembers(
        INamedTypeSymbol model,
        CancellationToken cancellationToken
    ) => GetSharedSparseMembers(model, cancellationToken);

    private static ImmutableArray<SymbolPreviousModelInfo> GetPreviousModels(
        INamedTypeSymbol model,
        string modelId,
        int modelVersion,
        CancellationToken cancellationToken,
        ImmutableArray<GeneratorDiagnosticInfo>.Builder diagnostics
    )
    {
        var previousModels = ImmutableArray.CreateBuilder<SymbolPreviousModelInfo>();
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
                new SymbolPreviousModelInfo(
                    previousModel,
                    GetMembers(previousModel, cancellationToken).ToImmutableArray()
                )
            );
        }

        return previousModels.ToImmutable();
    }
}
