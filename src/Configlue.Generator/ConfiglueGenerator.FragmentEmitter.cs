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
    private static void AppendFragment(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendLineAt(
            1,
            "/// <summary>A sparse, presence-aware representation of this model.</summary>"
        );
        code.AppendLineAt(
            1,
            "[global::System.Text.Json.Serialization.JsonConverter(typeof(FragmentJsonConverter))]"
        );
        code.AppendLineAt(
            1,
            "public sealed class Fragment : global::Configlue.IConfiglueFragment<Fragment>"
        );
        code.AppendLineAt(1, "{");
        foreach (var member in members)
        {
            code.AppendLineAt(
                2,
                "[global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]"
            );
            code.AppendIndent(2)
                .Append("public global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append("> ")
                .Append(EscapeIdentifier(member.Property.Name))
                .AppendLine(" { get; init; }");
        }

        code.AppendLine();
        code.AppendLineAt(
            2,
            "/// <summary>Whether this fragment has no present members.</summary>"
        );
        code.AppendIndent(2)
            .Append("public bool IsEmpty => ")
            .Append(
                members.Length == 0
                    ? "true"
                    : JoinMemberExpressions(
                        members,
                        static member =>
                            "!" + EscapeIdentifier(member.Property.Name) + ".IsPresent",
                        code.CancellationToken
                    )
            )
            .AppendLine(";");
        code.AppendLine();
        AppendFragmentDescriptor(code, modelType, members);
        AppendFromModel(code, modelType, members);
        AppendToModel(code, modelType, members);
        AppendMerge(code, members);
        AppendApplyChanges(code, members);
        AppendDiff(code, modelType, members);
        AppendFragmentClone(code, members);
        AppendPatchSupport(code, members);
        AppendJsonConverter(code, members);
        code.AppendLineAt(1, "}");
        AppendBuilder(code, members);
        AppendPatch(code, modelType, members);
    }

    private static void AppendFragmentDescriptor(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendLineAt(2, "public static Fragment Empty => new();");
        code.AppendIndent(2)
            .Append("public global::Configlue.ConfiglueModelSchema Schema => ")
            .Append(modelType)
            .AppendLine(".FragmentSchema;");
        code.AppendLineAt(
            2,
            "public global::System.Collections.Generic.IEnumerable<global::Configlue.ConfiglueFragmentMember> EnumeratePresentMembers()"
        );
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(3)
                .Append("if (")
                .Append(name)
                .Append(".IsPresent) yield return new(")
                .Append(member.Id)
                .Append(", ")
                .Append(SymbolDisplay.FormatLiteral(member.Property.Name, true))
                .Append(", ")
                .Append(name)
                .AppendLine(".Value);");
        }

        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "public global::Configlue.IConfiglueFragment WithMember(int memberId, object? value)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var builder = ToBuilder();");
        code.AppendLineAt(3, "switch (memberId)");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(4)
                .Append("case ")
                .Append(member.Id)
                .Append(": builder.")
                .Append(name)
                .Append(" = global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append(">.Present((")
                .Append(FragmentValueType(member))
                .AppendLine(")value!); break;");
        }

        code.AppendLineAt(
            4,
            "default: throw new global::System.ArgumentOutOfRangeException(nameof(memberId));"
        );
        code.AppendLineAt(3, "}");
        code.AppendLineAt(3, "return builder.Build();");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendFromModel(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendIndent(2)
            .Append("public static Fragment From(")
            .Append(modelType)
            .AppendLine(" value)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "global::System.ArgumentNullException.ThrowIfNull(value);");
        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var access = "value." + EscapeIdentifier(member.Property.Name);
            var value = member.ChildModel is null
                ? access
                : $"({access} is null ? null : {NonNullableTypeName(member.ChildModel)}.Fragment.From({access}))";
            code.AppendIndent(4)
                .Append(EscapeIdentifier(member.Property.Name))
                .Append(" = global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append(">.Present(")
                .Append(value)
                .AppendLine("),");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendToModel(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendIndent(2).Append("public ").Append(modelType).AppendLine(" ToModel()");
        code.AppendLineAt(2, "{");
        code.AppendIndent(3).Append("var defaults = new ").Append(modelType).AppendLine("();");
        code.AppendIndent(3).Append("return new ").Append(modelType).AppendLine();
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var value = member.ChildModel is null ? name + ".Value!" : name + ".Value?.ToModel()!";
            code.AppendIndent(4)
                .Append(name)
                .Append(" = ")
                .Append(name)
                .Append(".IsPresent ? ")
                .Append(value)
                .Append(" : defaults.")
                .Append(name)
                .AppendLine(",");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendMerge(IndentedStringBuilder code, ImmutableArray<MemberModel> members)
    {
        code.AppendLineAt(
            2,
            "/// <summary>Merges a higher-priority fragment over this fragment.</summary>"
        );
        code.AppendLineAt(2, "public Fragment Merge(Fragment higherPriority)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "global::System.ArgumentNullException.ThrowIfNull(higherPriority);");
        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var lower = "this." + name;
            var higher = "higherPriority." + name;
            string expression;
            if (member.MergeMode == 1 && member.ChildModel is not null)
            {
                expression =
                    $"{higher}.IsPresent ? global::Configlue.Optional<{FragmentValueType(member)}>.Present(({lower}.IsPresent && (object?){lower}.Value is not null && (object?){higher}.Value is not null) ? {lower}.Value!.Merge({higher}.Value!) : {higher}.Value) : {lower}";
            }
            else if (member.MergeMode is 2 or 3)
            {
                var merged = BuildCollectionMerge(member, lower + ".Value!", higher + ".Value!");
                expression =
                    $"{higher}.IsPresent ? ({lower}.IsPresent && (object?){lower}.Value is not null && (object?){higher}.Value is not null ? global::Configlue.Optional<{FragmentValueType(member)}>.Present({merged}) : {higher}) : {lower}";
            }
            else
            {
                expression = $"{higher}.IsPresent ? {higher} : {lower}";
            }

            code.AppendIndent(4).Append(name).Append(" = ").Append(expression).AppendLine(",");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendApplyChanges(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendLineAt(
            2,
            "/// <summary>Applies a sparse semantic diff to this source-local contribution.</summary>"
        );
        code.AppendLineAt(2, "public Fragment ApplyChanges(Fragment changes)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "global::System.ArgumentNullException.ThrowIfNull(changes);");
        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var type = FragmentValueType(member);
            var expression = member.ChildModel is null
                ? $"changes.{name}.IsPresent ? changes.{name} : this.{name}"
                : $"changes.{name}.IsPresent ? global::Configlue.Optional<{type}>.Present((this.{name}.IsPresent && (object?)this.{name}.Value is not null && (object?)changes.{name}.Value is not null) ? this.{name}.Value!.ApplyChanges(changes.{name}.Value!) : changes.{name}.Value) : this.{name}";
            code.AppendIndent(4).Append(name).Append(" = ").Append(expression).AppendLine(",");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendDiff(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members
    )
    {
        foreach (var member in members.Where(static member => member.ChildModel is not null))
        {
            var type = NonNullableTypeName(member.ChildModel!);
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(2)
                .Append("private static global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append("> __Diff_")
                .Append(name)
                .Append('(')
                .Append(type)
                .Append("? before, ")
                .Append(type)
                .AppendLine("? after)");
            code.AppendLineAt(2, "{");
            code.AppendLineAt(
                3,
                "if (global::System.Object.ReferenceEquals(before, after)) return default;"
            );
            code.AppendIndent(3)
                .Append("if (before is null || after is null) return global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append(">.Present(after is null ? null : ")
                .Append(type)
                .AppendLine(".Fragment.From(after));");
            code.AppendIndent(3)
                .Append("var difference = ")
                .Append(type)
                .AppendLine(".Fragment.Diff(before, after);");
            code.AppendIndent(3)
                .Append("return difference.IsEmpty ? default : global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .AppendLine(">.Present(difference); ");
            code.AppendLineAt(2, "}");
        }

        code.AppendLineAt(
            2,
            "/// <summary>Creates a sparse semantic diff between two ordinary model values.</summary>"
        );
        code.AppendIndent(2)
            .Append("public static Fragment Diff(")
            .Append(modelType)
            .Append(" before, ")
            .Append(modelType)
            .AppendLine(" after)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "global::System.ArgumentNullException.ThrowIfNull(before);");
        code.AppendLineAt(3, "global::System.ArgumentNullException.ThrowIfNull(after);");
        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var before = "before." + name;
            var after = "after." + name;
            var valueType = FragmentValueType(member);
            var condition = member.ChildModel is null
                ? $"global::Configlue.ConfiglueValueComparer.AreEqual({before}, {after}) ? default : global::Configlue.Optional<{valueType}>.Present({after})"
                : $"__Diff_{name}({before}, {after})";
            code.AppendIndent(4).Append(name).Append(" = ").Append(condition).AppendLine(",");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendFragmentClone(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendLineAt(
            2,
            "/// <summary>Copies the fragment and its generated nested values.</summary>"
        );
        code.AppendLineAt(2, "public Fragment DeepClone() => new()");
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var type = FragmentValueType(member);
            var expression = CloneFragmentExpression(
                member,
                "this." + name + ".Value",
                code.CancellationToken
            );
            code.AppendIndent(3)
                .Append(name)
                .Append(" = this.")
                .Append(name)
                .Append(".IsPresent ? global::Configlue.Optional<")
                .Append(type)
                .Append(">.Present(")
                .Append(expression)
                .Append(" ) : default,")
                .AppendLine();
        }

        code.AppendLineAt(2, "};");
        code.AppendLine();
    }

    private static void AppendPatchSupport(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendLineAt(
            2,
            "/// <summary>Applies source-local set and unset operations to this fragment.</summary>"
        );
        code.AppendLineAt(2, "public Fragment Apply(Patch patch)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "global::System.ArgumentNullException.ThrowIfNull(patch);");
        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(4)
                .Append(name)
                .Append(" = patch.")
                .Append(name)
                .Append(".Apply(this.")
                .Append(name)
                .AppendLine("),");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
        code.AppendLineAt(
            2,
            "/// <summary>Creates a mutable builder initialized from this fragment.</summary>"
        );
        code.AppendLineAt(2, "public FragmentBuilder ToBuilder() => new(this);");
        code.AppendLine();
        code.AppendLineAt(
            2,
            "/// <summary>Creates a source-local set patch from all present members.</summary>"
        );
        code.AppendLineAt(2, "public Patch ToPatch() => new(this);");
    }

    private static void AppendJsonConverter(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
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
            "if (reader.TokenType != global::System.Text.Json.JsonTokenType.StartObject) throw new global::System.Text.Json.JsonException(\"A fragment must be a JSON object.\");"
        );
        code.AppendLineAt(4, "var builder = new FragmentBuilder();");
        code.AppendLineAt(4, "while (reader.Read())");
        code.AppendLineAt(4, "{");
        code.AppendLineAt(
            5,
            "if (reader.TokenType == global::System.Text.Json.JsonTokenType.EndObject) return builder.Build();"
        );
        code.AppendLineAt(
            5,
            "if (reader.TokenType != global::System.Text.Json.JsonTokenType.PropertyName) throw new global::System.Text.Json.JsonException(\"Expected a fragment property name.\");"
        );
        code.AppendLineAt(5, "var propertyName = reader.GetString();");
        code.AppendLineAt(
            5,
            "if (!reader.Read()) throw new global::System.Text.Json.JsonException(\"Unexpected end of fragment.\");"
        );
        if (members.Length > 0)
        {
            var first = true;
            foreach (var member in members)
            {
                var property = EscapeIdentifier(member.Property.Name);
                var wireName = GetJsonPropertyName(
                    member.Property,
                    code.CancellationToken,
                    out var explicitName
                );
                code.AppendIndent(5)
                    .Append(first ? "if (" : "else if (")
                    .Append("Matches(propertyName, ")
                    .Append(SymbolDisplay.FormatLiteral(wireName, true))
                    .Append(", ")
                    .Append(explicitName ? "false" : "true")
                    .AppendLine(", options))");
                code.AppendIndent(6)
                    .Append("builder.")
                    .Append(property)
                    .Append(" = global::Configlue.Optional<")
                    .Append(FragmentValueType(member))
                    .Append(">.Present(global::System.Text.Json.JsonSerializer.Deserialize<")
                    .Append(FragmentValueType(member))
                    .AppendLine(">(ref reader, options));");
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
            var wireName = GetJsonPropertyName(
                member.Property,
                code.CancellationToken,
                out var explicitName
            );
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

            code.AppendIndent(5)
                .Append("global::System.Text.Json.JsonSerializer.Serialize<")
                .Append(FragmentValueType(member))
                .Append(">(writer, value.")
                .Append(property)
                .AppendLine(".Value!, options);");
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
        code.AppendLineAt(4, "if (actual is null) return false;");
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

    private static void AppendBuilder(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendLineAt(1, "/// <summary>A mutable builder for a generated fragment.</summary>");
        code.AppendLineAt(1, "public sealed class FragmentBuilder");
        code.AppendLineAt(1, "{");
        foreach (var member in members)
        {
            code.AppendIndent(2)
                .Append("public global::Configlue.Optional<")
                .Append(FragmentValueType(member))
                .Append("> ")
                .Append(EscapeIdentifier(member.Property.Name))
                .AppendLine(" { get; set; }");
        }

        code.AppendLineAt(2, "public FragmentBuilder() { }");
        code.AppendLineAt(2, "internal FragmentBuilder(Fragment fragment)");
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(3).Append(name).Append(" = fragment.").Append(name).AppendLine(";");
        }

        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "public Fragment Build() => new()");
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(3).Append(name).Append(" = ").Append(name).AppendLine(",");
        }

        code.AppendLineAt(2, "};");
        code.AppendLineAt(1, "}");
    }

    private static void AppendPatch(
        IndentedStringBuilder code,
        string modelType,
        ImmutableArray<MemberModel> members
    )
    {
        code.AppendLineAt(
            1,
            "/// <summary>A source-local set/unset patch for generated fragment members.</summary>"
        );
        code.AppendLineAt(1, "public sealed class Patch : global::Configlue.IConfigluePatch");
        code.AppendLineAt(1, "{");
        code.AppendIndent(2)
            .Append("public global::Configlue.ConfiglueModelSchema Schema => ")
            .Append(modelType)
            .AppendLine(".ConfiglueSchema;");
        foreach (var member in members)
        {
            code.AppendIndent(2)
                .Append("public global::Configlue.FragmentOperation<")
                .Append(FragmentValueType(member))
                .Append("> ")
                .Append(EscapeIdentifier(member.Property.Name))
                .AppendLine(" { get; set; }");
        }

        code.AppendLineAt(2, "public Patch() { }");
        code.AppendLineAt(2, "internal Patch(Fragment fragment)");
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(3)
                .Append(name)
                .Append(" = fragment.")
                .Append(name)
                .Append(".IsPresent ? global::Configlue.FragmentOperation<")
                .Append(FragmentValueType(member))
                .Append(">.Set(fragment.")
                .Append(name)
                .AppendLine(".Value) : default;");
        }

        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "public bool IsEmpty => ");
        code.AppendIndent(3)
            .Append(
                members.Length == 0
                    ? "true"
                    : JoinMemberExpressions(
                        members,
                        static member =>
                            EscapeIdentifier(member.Property.Name)
                            + ".Kind == global::Configlue.FragmentOperationKind.Unchanged",
                        code.CancellationToken
                    )
            )
            .AppendLine(";");
        code.AppendLineAt(
            2,
            "public global::Configlue.IConfiglueFragment Apply(global::Configlue.IConfiglueFragment fragment)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if (fragment is not Fragment typed) throw new global::System.ArgumentException(\"The patch can only be applied to its generated fragment type.\", nameof(fragment));"
        );
        code.AppendLineAt(3, "return typed.Apply(this);");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(1, "}");
    }
}
