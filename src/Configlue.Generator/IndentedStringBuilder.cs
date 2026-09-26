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

    public IndentedStringBuilder AppendIndent(int level)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (level < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        _builder.Append(' ', level * 4);
        return this;
    }

    public IndentedStringBuilder Append(string value)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _builder.Append(value);
        return this;
    }

    public IndentedStringBuilder Append(char value)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _builder.Append(value);
        return this;
    }

    public IndentedStringBuilder Append(int value)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _builder.Append(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    public IndentedStringBuilder AppendLine(string value = "")
    {
        _cancellationToken.ThrowIfCancellationRequested();
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
