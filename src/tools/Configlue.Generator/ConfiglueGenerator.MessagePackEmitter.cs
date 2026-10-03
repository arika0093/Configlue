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
    private static void AppendMessagePackFormatter(
        IndentedStringBuilder code,
        ImmutableArray<MemberModel> members
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            2,
            "/// <summary>Reads and writes the sparse fragment as a member-name keyed MessagePack map.</summary>"
        );
        code.AppendLineAt(
            2,
            "public sealed class FragmentMessagePackFormatter : global::MessagePack.Formatters.IMessagePackFormatter<Fragment>"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "public void Serialize(ref global::MessagePack.MessagePackWriter writer, Fragment value, global::MessagePack.MessagePackSerializerOptions options)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "if (value is null) { writer.WriteNil(); return; }");
        var countExpression =
            members.Length == 0
                ? "0"
                : string.Join(
                    " + ",
                    members.Select(member =>
                        "(value." + EscapeIdentifier(member.Property.Name) + ".IsPresent ? 1 : 0)"
                    )
                );
        code.AppendLineAt(4, "writer.WriteMapHeader(" + countExpression + ");");
        foreach (var member in members)
        {
            code.CancellationToken.ThrowIfCancellationRequested();
            var property = EscapeIdentifier(member.Property.Name);
            code.AppendIndent(4).Append("if (value.").Append(property).AppendLine(".IsPresent)");
            code.AppendLineAt(4, "{");
            code.AppendLineAt(
                5,
                "writer.Write(" + SymbolDisplay.FormatLiteral(member.Property.Name, true) + ");"
            );
            if (member.ChildModel is null)
            {
                code.AppendIndent(5)
                    .Append("(options.Resolver.GetFormatter<")
                    .Append(FragmentValueType(member))
                    .Append(
                        ">() ?? throw new global::MessagePack.MessagePackSerializationException(\"No MessagePack formatter is registered for '"
                    )
                    .Append(FragmentValueType(member))
                    .AppendLine("'.\")).Serialize(ref writer, value.")
                    .Append(property)
                    .AppendLine(".Value!, options);");
            }
            else
            {
                var childFragment = member.ChildFragmentType!;
                code.AppendIndent(5)
                    .Append("if (value.")
                    .Append(property)
                    .AppendLine(".Value is null)");
                code.AppendLineAt(5, "{ writer.WriteNil(); }");
                code.AppendLineAt(5, "else");
                code.AppendLineAt(5, "{");
                code.AppendIndent(6)
                    .Append(childFragment)
                    .Append(".MessagePackFormatter.Serialize(ref writer, value.")
                    .Append(property)
                    .AppendLine(".Value, options);");
                code.AppendLineAt(5, "}");
            }

            code.AppendLineAt(4, "}");
        }

        code.AppendLineAt(3, "}");
        code.AppendLine();
        code.AppendLineAt(
            3,
            "public Fragment Deserialize(ref global::MessagePack.MessagePackReader reader, global::MessagePack.MessagePackSerializerOptions options)"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "if (reader.TryReadNil()) { return null!; }");
        code.AppendLineAt(4, "options.Security.DepthStep(ref reader);");
        code.AppendLineAt(4, "try");
        code.AppendLineAt(4, "{");
        code.AppendLineAt(5, "var length = reader.ReadMapHeader();");
        code.AppendLineAt(5, "var builder = new FragmentBuilder();");
        code.AppendLineAt(5, "for (var index = 0; index < length; index++)");
        code.AppendLineAt(5, "{");
        code.AppendLineAt(6, "var memberName = reader.ReadString();");
        code.AppendLineAt(6, "switch (memberName)");
        code.AppendLineAt(6, "{");
        foreach (var member in members)
        {
            code.CancellationToken.ThrowIfCancellationRequested();
            var property = EscapeIdentifier(member.Property.Name);
            code.AppendLineAt(
                7,
                "case " + SymbolDisplay.FormatLiteral(member.Property.Name, true) + ":"
            );
            if (member.ChildModel is null)
            {
                code.AppendIndent(8)
                    .Append("builder.")
                    .Append(property)
                    .Append(" = global::Configlue.Optional<")
                    .Append(FragmentValueType(member))
                    .Append(">.Present((options.Resolver.GetFormatter<")
                    .Append(FragmentValueType(member))
                    .Append(
                        ">() ?? throw new global::MessagePack.MessagePackSerializationException(\"No MessagePack formatter is registered for '"
                    )
                    .Append(FragmentValueType(member))
                    .AppendLine("'.\")).Deserialize(ref reader, options));");
            }
            else
            {
                var childFragment = member.ChildFragmentType!;
                code.AppendIndent(8)
                    .Append("builder.")
                    .Append(property)
                    .Append(" = global::Configlue.Optional<")
                    .Append(FragmentValueType(member))
                    .Append(">.Present(")
                    .Append(childFragment)
                    .AppendLine(".MessagePackFormatter.Deserialize(ref reader, options));");
            }

            code.AppendLineAt(8, "break;");
        }

        code.AppendLineAt(7, "default: reader.Skip(); break;");
        code.AppendLineAt(6, "}");
        code.AppendLineAt(5, "}");
        code.AppendLineAt(5, "return builder.Build();");
        code.AppendLineAt(4, "}");
        code.AppendLineAt(4, "finally { reader.Depth--; }");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(2, "}");
    }
}
