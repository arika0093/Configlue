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
            "public sealed class FragmentJsonConverter : global::System.Text.Json.Serialization.JsonConverter<Fragment>"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "public override Fragment Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)"
        );
        code.AppendLineAt(3, "{");
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
        if (members.Length > 0)
        {
            var first = true;
            foreach (var member in members)
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
                            ">.Present(global::System.Text.Json.JsonSerializer.Deserialize(ref reader, (global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<"
                        )
                        .Append(FragmentValueType(member))
                        .Append(">)options.GetTypeInfo(typeof(")
                        .Append(FragmentRuntimeValueType(member))
                        .AppendLine("))));");
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

            code.AppendLineAt(5, "else reader.Skip();");
        }
        else
        {
            code.AppendLineAt(5, "else reader.Skip();");
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
        foreach (var member in members)
        {
            var property = EscapeIdentifier(member.Property.Name);
            var wireName = member.Property.JsonPropertyName!;
            var explicitName = member.Property.HasExplicitJsonPropertyName;
            code.AppendIndent(4).Append("if (value.").Append(property).AppendLine(".IsPresent)");
            code.AppendLineAt(4, "{");
            if (explicitName)
            {
                code.AppendIndent(5)
                    .Append("writer.WritePropertyName(")
                    .Append(SymbolDisplay.FormatLiteral(wireName, true))
                    .AppendLine(");");
            }
            else
            {
                code.AppendIndent(5)
                    .Append("writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(")
                    .Append(SymbolDisplay.FormatLiteral(wireName, true))
                    .Append(") ?? ")
                    .Append(SymbolDisplay.FormatLiteral(wireName, true))
                    .AppendLine(");");
            }

            if (member.ChildModel is null)
            {
                code.AppendIndent(5)
                    .Append("global::System.Text.Json.JsonSerializer.Serialize<")
                    .Append(FragmentValueType(member))
                    .Append(">(writer, value.")
                    .Append(property)
                    .Append(
                        ".Value!, (global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<"
                    )
                    .Append(FragmentValueType(member))
                    .Append(">)options.GetTypeInfo(typeof(")
                    .Append(FragmentRuntimeValueType(member))
                    .AppendLine(")));");
            }
            else
            {
                var childFragment = member.ChildFragmentType!;
                code.AppendIndent(5)
                    .Append("if (value.")
                    .Append(property)
                    .AppendLine(".Value is null)");
                code.AppendLineAt(5, "{ writer.WriteNullValue(); }");
                code.AppendIndent(5).AppendLine("else");
                code.AppendLineAt(5, "{");
                code.AppendIndent(6)
                    .Append(childFragment)
                    .Append(".JsonConverter.Write(writer, value.")
                    .Append(property)
                    .AppendLine(".Value, options);");
                code.AppendLineAt(5, "}");
            }
            code.AppendLineAt(4, "}");
        }

        code.AppendLineAt(4, "writer.WriteEndObject();");
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
        code.AppendLineAt(2, "}");
    }
}
