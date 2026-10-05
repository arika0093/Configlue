using System.Text;
using System.Text.Json;

namespace Configlue.DevTools;

/// <summary>
/// Line/column tracking writer for a single viewer JSON projection.
/// </summary>
/// <remarks>
/// Position and range bookkeeping only. No knowledge of provenance,
/// editability, secrets, or JSON Schema.
/// </remarks>
internal sealed class ConfiglueViewerPositionWriter
{
    private static readonly JsonSerializerOptions QuoteOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    private readonly StringBuilder _builder = new();
    private readonly string _indent;

    public ConfiglueViewerPositionWriter(string indent)
    {
        _indent = string.IsNullOrEmpty(indent) ? "  " : indent;
    }

    public int Line { get; private set; } = 1;

    public int Column { get; private set; } = 1;

    public (int Line, int Column) Position => (Line, Column);

    public void Append(string text)
    {
        foreach (var ch in text)
        {
            _builder.Append(ch);
            if (ch == '\n')
            {
                Line++;
                Column = 1;
            }
            else if (ch != '\r')
            {
                Column++;
            }
        }
    }

    public void AppendLine()
    {
        _builder.Append('\n');
        Line++;
        Column = 1;
    }

    public void AppendIndent(int depth)
    {
        for (var index = 0; index < depth; index++)
        {
            Append(_indent);
        }
    }

    public void AppendQuoted(string value)
    {
        Append(JsonSerializer.Serialize(value, QuoteOptions));
    }

    public static ConfiglueViewerRange CaptureRange(
        (int Line, int Column) start,
        (int Line, int Column) end
    )
    {
        return new ConfiglueViewerRange(start.Line, start.Column, end.Line, end.Column);
    }

    public static ConfiglueViewerRange EmptyRangeAt((int Line, int Column) position)
    {
        return new ConfiglueViewerRange(
            position.Line,
            position.Column,
            position.Line,
            position.Column
        );
    }

    public override string ToString() => _builder.ToString();
}
