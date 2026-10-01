using System;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Configlue.Generator;

internal sealed class IndentedStringBuilder
{
    private readonly StringBuilder _builder = new();
    private readonly CancellationToken _cancellationToken;

    public IndentedStringBuilder(CancellationToken cancellationToken) =>
        _cancellationToken = cancellationToken;

    public CancellationToken CancellationToken => _cancellationToken;

    public int IndentOffset { get; set; }

    public IndentedStringBuilder AppendIndent(int level)
    {
        var effective = level + IndentOffset;
        if (effective < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        _builder.Append(' ', effective * 4);
        return this;
    }

    public IndentedStringBuilder Append(string value)
    {
        _builder.Append(value);
        return this;
    }

    public IndentedStringBuilder Append(char value)
    {
        _builder.Append(value);
        return this;
    }

    public IndentedStringBuilder Append(int value)
    {
        _builder.Append(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    public IndentedStringBuilder AppendLine(string value = "")
    {
        _builder.Append(value).Append('\n');
        return this;
    }

    public IndentedStringBuilder AppendLineAt(int level, string value)
    {
        AppendIndent(level);
        return AppendLine(value);
    }

    public override string ToString()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return _builder.ToString();
    }
}
