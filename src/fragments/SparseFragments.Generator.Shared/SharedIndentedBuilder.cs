using System;
using System.Globalization;
using System.Text;
using System.Threading;

namespace SparseFragments.Generator.Shared;

internal sealed class SharedIndentedBuilder
{
    private readonly StringBuilder _builder = new();
    private readonly CancellationToken _cancellationToken;

    public SharedIndentedBuilder(CancellationToken cancellationToken) =>
        _cancellationToken = cancellationToken;

    public CancellationToken CancellationToken => _cancellationToken;

    public int IndentOffset { get; set; }

    public SharedIndentedBuilder AppendIndent(int level)
    {
        var effective = level + IndentOffset;
        if (effective < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        _builder.Append(' ', effective * 4);
        return this;
    }

    public SharedIndentedBuilder Append(string value)
    {
        _builder.Append(value);
        return this;
    }

    public SharedIndentedBuilder Append(char value)
    {
        _builder.Append(value);
        return this;
    }

    public SharedIndentedBuilder Append(int value)
    {
        _builder.Append(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    public SharedIndentedBuilder AppendLine(string value = "")
    {
        _builder.Append(value).Append('\n');
        return this;
    }

    public SharedIndentedBuilder AppendLineAt(int level, string value)
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
