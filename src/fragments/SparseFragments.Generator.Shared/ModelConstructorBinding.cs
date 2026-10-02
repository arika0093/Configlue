using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SparseFragments.Generator.Shared;

internal readonly record struct ConstructorParameterBinding(
    string PropertyName,
    string DefaultExpression
);

/// <summary>Constructor parameters bound by property name and exact type.</summary>
internal sealed record ModelConstructorBinding(
    ImmutableArray<ConstructorParameterBinding> Parameters
)
{
    public static ModelConstructorBinding Parameterless { get; } =
        new(ImmutableArray<ConstructorParameterBinding>.Empty);

    public bool Equals(ModelConstructorBinding? other) =>
        other is not null && Parameters.SequenceEqual(other.Parameters);

    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var parameter in Parameters)
            hash = unchecked(hash * 31 + parameter.GetHashCode());
        return hash;
    }

    public static ModelConstructorBinding? AnalyzeRoot(
        INamedTypeSymbol model,
        CancellationToken cancellationToken
    )
    {
        var properties = SparseModelAnalyzer
            .GetReadableProperties(model, cancellationToken)
            .Where(static property =>
                property.SetMethod?.DeclaredAccessibility == Accessibility.Public
            )
            .ToArray();
        foreach (
            var constructorParameters in model
                .InstanceConstructors.OrderBy(static constructor => constructor.Parameters.Length)
                .ThenBy(static constructor => constructor.ToDisplayString(), StringComparer.Ordinal)
                .Select(static constructor => constructor.Parameters)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (constructorParameters.IsEmpty)
                return Parameterless;
            var parameters = ImmutableArray.CreateBuilder<ConstructorParameterBinding>();
            foreach (var parameter in constructorParameters)
            {
                var matches = properties
                    .Where(property =>
                        string.Equals(
                            parameter.Name,
                            property.Name,
                            StringComparison.OrdinalIgnoreCase
                        ) && SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type)
                    )
                    .ToArray();
                if (parameter.RefKind != RefKind.None || matches.Length != 1)
                    break;
                parameters.Add(
                    new ConstructorParameterBinding(matches[0].Name, DefaultExpression(parameter))
                );
            }
            if (parameters.Count == constructorParameters.Length)
                return new(parameters.ToImmutable());
        }
        return model.IsValueType ? Parameterless : null;
    }

    private static string DefaultExpression(IParameterSymbol parameter)
    {
        var typeName = SparseNaming.TypeName(parameter.Type);
        if (!parameter.HasExplicitDefaultValue || parameter.ExplicitDefaultValue is null)
            return "default(" + typeName + ")!";
        var value = parameter.ExplicitDefaultValue;
        if (value is string text)
            return SymbolDisplay.FormatLiteral(text, true);
        if (value is char character)
            return SymbolDisplay.FormatLiteral(character, true);
        if (value is bool boolean)
            return boolean ? "true" : "false";
        var literal = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        if (
            value is float specialSingle
            && (float.IsNaN(specialSingle) || float.IsInfinity(specialSingle))
        )
        {
            var field = specialSingle > 0 ? "PositiveInfinity" : "NegativeInfinity";
            if (float.IsNaN(specialSingle))
                field = "NaN";
            return "global::System.Single." + field;
        }
        if (
            value is double specialDouble
            && (double.IsNaN(specialDouble) || double.IsInfinity(specialDouble))
        )
        {
            var field = specialDouble > 0 ? "PositiveInfinity" : "NegativeInfinity";
            if (double.IsNaN(specialDouble))
                field = "NaN";
            return "global::System.Double." + field;
        }
        if (value is float single)
            literal = single.ToString("R", CultureInfo.InvariantCulture) + "F";
        if (value is double number)
            literal = number.ToString("R", CultureInfo.InvariantCulture) + "D";
        if (value is decimal)
            literal += "M";
        return "(" + typeName + ")(" + literal + ")";
    }
}
