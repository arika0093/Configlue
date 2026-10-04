using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    private static void AppendJsonConverter(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            2,
            "/// <summary>Reads and writes sparse fragment properties without materializing absent values.</summary>"
        );
        code.AppendLineAt(
            2,
            "/// <remarks>Members marked with <c>JsonIgnore(Condition = Always)</c> (including plain <c>[JsonIgnore]</c>) are never written and incoming values for those JSON names are skipped, even when strict unmapped-member handling is enabled. <c>Condition = Never</c> keeps the member in the payload. <c>WhenWritingNull</c>/<c>WhenWritingDefault</c> only suppress writing a present value that is null/default; a missing <c>Optional</c> stays missing and an explicit JSON value is still read.</remarks>"
        );
        code.AppendLineAt(
            2,
            "public sealed class FragmentJsonConverter : global::System.Text.Json.Serialization.JsonConverter<Fragment>, global::Configlue.Provider.Json.IJsonObjectPayloadWriter"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "private static global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<TMember> GetMemberTypeInfo<TMember>(global::System.Text.Json.JsonSerializerOptions options)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "try");
        code.AppendLineAt(4, "{");
        code.AppendLineAt(
            5,
            "return (global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<TMember>)options.GetTypeInfo(typeof(TMember));"
        );
        code.AppendLineAt(4, "}");
        code.AppendLineAt(4, "catch (global::System.NotSupportedException exception)");
        code.AppendLineAt(4, "{");
        code.AppendLineAt(
            5,
            "throw new global::System.InvalidOperationException(\"Configlue's generated fragment converter requires JsonTypeInfo metadata for member type '\" + typeof(TMember) + \"'. Add the model/member types to a source-generated JsonSerializerContext and set it as JsonSerializerOptions.TypeInfoResolver.\", exception);"
        );
        code.AppendLineAt(4, "}");
        code.AppendLineAt(3, "}");
        code.AppendLine();
        code.AppendLineAt(
            3,
            "public override Fragment Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "ValidateJsonNames(options);");
        code.AppendLineAt(
            4,
            "if (reader.TokenType != global::System.Text.Json.JsonTokenType.StartObject) { throw new global::System.Text.Json.JsonException(\"A fragment must be a JSON object.\"); }"
        );
        code.AppendLineAt(4, "var builder = new FragmentBuilder();");
        code.AppendLineAt(4, "while (reader.Read())");
        code.AppendLineAt(4, "{");
        code.AppendLineAt(
            5,
            "if (reader.TokenType == global::System.Text.Json.JsonTokenType.EndObject) { return builder.Build(); }"
        );
        code.AppendLineAt(
            5,
            "if (reader.TokenType != global::System.Text.Json.JsonTokenType.PropertyName) { throw new global::System.Text.Json.JsonException(\"Expected a fragment property name.\"); }"
        );
        code.AppendLineAt(5, "var propertyName = reader.GetString();");
        code.AppendLineAt(
            5,
            "if (!reader.Read()) { throw new global::System.Text.Json.JsonException(\"Unexpected end of fragment.\"); }"
        );
        // Only JSON-persisted members participate in read/write. Members with
        // JsonIgnore(Condition = Always) stay in the fragment algebra (merge, patch,
        // ToModel/From) but are skipped here on both directions.
        var jsonMembers = members
            .Where(static member => !member.Property.IsJsonIgnored)
            .ToImmutableArray();
        var ignoredMembers = members
            .Where(static member => member.Property.IsJsonIgnored)
            .ToImmutableArray();
        if (jsonMembers.Length + ignoredMembers.Length > 0)
        {
            var first = true;
            foreach (var member in jsonMembers)
            {
                var property = EscapeIdentifier(member.Property.Name);
                var wireName = member.Property.JsonPropertyName!;
                var explicitName = member.Property.HasExplicitJsonPropertyName;
                code.AppendIndent(5)
                    .Append(first ? "if (" : "else if (")
                    .Append("Matches(propertyName, ")
                    .Append(SymbolDisplay.FormatLiteral(wireName, true))
                    .Append(", ")
                    .Append(explicitName ? "false" : "true")
                    .AppendLine(", options))");
                if (member.ChildModel is null)
                {
                    code.AppendIndent(6)
                        .Append("builder.")
                        .Append(property)
                        .Append(" = global::Configlue.Optional<")
                        .Append(FragmentValueType(member))
                        .Append(
                            ">.Present(global::System.Text.Json.JsonSerializer.Deserialize(ref reader, GetMemberTypeInfo<"
                        )
                        .Append(FragmentValueType(member))
                        .AppendLine(">(options)));");
                }
                else
                {
                    var childFragment = member.ChildFragmentType!;
                    code.AppendIndent(6)
                        .Append("builder.")
                        .Append(property)
                        .Append(" = global::Configlue.Optional<")
                        .Append(FragmentValueType(member))
                        .Append(
                            ">.Present(reader.TokenType == global::System.Text.Json.JsonTokenType.Null ? null : "
                        )
                        .Append(childFragment)
                        .AppendLine(
                            ".JsonConverter.Read(ref reader, typeof("
                                + childFragment
                                + "), options));"
                        );
                }
                first = false;
            }

            // Ignored members are known JSON names that never populate the fragment.
            // Skip their values instead of routing them to unknown-property handling so
            // strict UnmappedMemberHandling.Disallow keeps accepting them, matching
            // System.Text.Json's treatment of ignored properties.
            foreach (var ignored in ignoredMembers.Select(static member => member.Property))
            {
                var ignoredWireName = ignored.JsonPropertyName!;
                var ignoredExplicit = ignored.HasExplicitJsonPropertyName;
                code.AppendIndent(5)
                    .Append(first ? "if (" : "else if (")
                    .Append("Matches(propertyName, ")
                    .Append(SymbolDisplay.FormatLiteral(ignoredWireName, true))
                    .Append(", ")
                    .Append(ignoredExplicit ? "false" : "true")
                    .AppendLine(", options))");
                code.AppendLineAt(6, "{ reader.Skip(); }");
                first = false;
            }

            code.AppendLineAt(
                5,
                "else HandleUnknownFragmentProperty(ref reader, options, propertyName);"
            );
        }
        else
        {
            code.AppendLineAt(
                5,
                "HandleUnknownFragmentProperty(ref reader, options, propertyName);"
            );
        }

        code.AppendLineAt(4, "}");
        code.AppendLineAt(
            4,
            "throw new global::System.Text.Json.JsonException(\"Unexpected end of fragment.\");"
        );
        code.AppendLineAt(3, "}");
        code.AppendLine();
        code.AppendLineAt(
            3,
            "public override void Write(global::System.Text.Json.Utf8JsonWriter writer, Fragment value, global::System.Text.Json.JsonSerializerOptions options)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "writer.WriteStartObject();");
        code.AppendLineAt(4, "WriteObjectPayloadProperties(writer, value, options);");
        code.AppendLineAt(4, "writer.WriteEndObject();");
        code.AppendLineAt(3, "}");
        code.AppendLine();
        code.AppendLineAt(
            3,
            "public void WriteObjectPayloadProperties(global::System.Text.Json.Utf8JsonWriter writer, object value, global::System.Text.Json.JsonSerializerOptions options)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "ValidateJsonNames(options);");
        code.AppendLineAt(
            4,
            "if (value is not Fragment typedValue) { throw new global::System.ArgumentException(\"The JSON payload value must be a generated fragment.\", nameof(value)); }"
        );
        foreach (var member in jsonMembers)
        {
            var property = EscapeIdentifier(member.Property.Name);
            var wireName = member.Property.JsonPropertyName!;
            var explicitName = member.Property.HasExplicitJsonPropertyName;
            code.AppendIndent(4)
                .Append("if (typedValue.")
                .Append(property)
                .AppendLine(".IsPresent)");
            code.AppendLineAt(4, "{");
            // Conditional ignores keep the Optional missing/present distinction: a
            // missing member is already excluded by IsPresent above, while a present
            // null/default value is omitted from JSON but still round-trips as missing.
            var conditionalIndent = 5;
            if (member.Property.IsJsonIgnoreWhenWritingNull)
            {
                code.AppendIndent(5)
                    .Append("if (typedValue.")
                    .Append(property)
                    .AppendLine(".Value is not null)");
                code.AppendLineAt(5, "{");
                conditionalIndent = 6;
            }
            else if (member.Property.IsJsonIgnoreWhenWritingDefault)
            {
                code.AppendIndent(5)
                    .Append(
                        "if (!global::System.Collections.Generic.EqualityComparer<"
                            + FragmentValueType(member)
                            + ">.Default.Equals(typedValue."
                    )
                    .Append(property)
                    .AppendLine(".Value, default))");
                code.AppendLineAt(5, "{");
                conditionalIndent = 6;
            }
            code.AppendIndent(conditionalIndent)
                .Append("var jsonPropertyName = ")
                .Append(
                    explicitName
                        ? SymbolDisplay.FormatLiteral(wireName, true)
                        : "options.PropertyNamingPolicy?.ConvertName("
                            + SymbolDisplay.FormatLiteral(wireName, true)
                            + ") ?? "
                            + SymbolDisplay.FormatLiteral(wireName, true)
                )
                .AppendLine(";");
            code.AppendLineAt(conditionalIndent, "writer.WritePropertyName(jsonPropertyName);");

            if (member.ChildModel is null)
            {
                code.AppendIndent(conditionalIndent)
                    .Append("global::System.Text.Json.JsonSerializer.Serialize<")
                    .Append(FragmentValueType(member))
                    .Append(">(writer, typedValue.")
                    .Append(property)
                    .Append(".Value!, GetMemberTypeInfo<")
                    .Append(FragmentValueType(member))
                    .AppendLine(">(options));");
            }
            else
            {
                var childFragment = member.ChildFragmentType!;
                code.AppendIndent(conditionalIndent)
                    .Append("if (typedValue.")
                    .Append(property)
                    .AppendLine(".Value is null)");
                code.AppendLineAt(conditionalIndent, "{ writer.WriteNullValue(); }");
                code.AppendIndent(conditionalIndent).AppendLine("else");
                code.AppendLineAt(conditionalIndent, "{");
                code.AppendIndent(conditionalIndent + 1)
                    .Append(childFragment)
                    .Append(".JsonConverter.Write(writer, typedValue.")
                    .Append(property)
                    .AppendLine(".Value, options);");
                code.AppendLineAt(conditionalIndent, "}");
            }
            if (conditionalIndent > 5)
            {
                code.AppendLineAt(5, "}");
            }
            code.AppendLineAt(4, "}");
        }

        code.AppendLineAt(3, "}");
        code.AppendLine();
        code.AppendLineAt(
            3,
            "private static bool Matches(string? actual, string propertyName, bool useNamingPolicy, global::System.Text.Json.JsonSerializerOptions options)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "if (actual is null) { return false; }");
        code.AppendLineAt(
            4,
            "var expected = useNamingPolicy ? options.PropertyNamingPolicy?.ConvertName(propertyName) ?? propertyName : propertyName;"
        );
        code.AppendLineAt(
            4,
            "return global::System.String.Equals(actual, expected, options.PropertyNameCaseInsensitive ? global::System.StringComparison.OrdinalIgnoreCase : global::System.StringComparison.Ordinal);"
        );
        code.AppendLineAt(3, "}");
        code.AppendLine();
        code.AppendLineAt(
            3,
            "private static void HandleUnknownFragmentProperty(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Text.Json.JsonSerializerOptions options, string? propertyName)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(
            4,
            "if (options.UnmappedMemberHandling == global::System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow) { throw new global::System.Text.Json.JsonException(\"The JSON property '\" + propertyName + \"' could not be mapped to fragment '\" + typeof(Fragment) + \"'.\"); }"
        );
        code.AppendLineAt(4, "reader.Skip();");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(
            3,
            "private static void ValidateJsonNames(global::System.Text.Json.JsonSerializerOptions options)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(
            4,
            "var names = new global::System.Collections.Generic.HashSet<string>(options.PropertyNameCaseInsensitive ? global::System.StringComparer.OrdinalIgnoreCase : global::System.StringComparer.Ordinal);"
        );
        foreach (var property in jsonMembers.Select(static member => member.Property))
        {
            var literal = SymbolDisplay.FormatLiteral(property.JsonPropertyName!, true);
            var expression = property.HasExplicitJsonPropertyName
                ? literal
                : "options.PropertyNamingPolicy?.ConvertName(" + literal + ") ?? " + literal;
            code.AppendLineAt(
                4,
                "if (!names.Add("
                    + expression
                    + ")) { throw new global::System.Text.Json.JsonException(\"Multiple fragment members map to the same JSON property name.\"); }"
            );
        }
        code.AppendLineAt(3, "}");
        code.AppendLineAt(2, "}");
    }
}
