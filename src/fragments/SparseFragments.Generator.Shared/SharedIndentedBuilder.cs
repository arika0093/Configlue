using System;
using System.Globalization;
using System.Text;
using System.Threading;

namespace SparseFragments.Generator.Shared;

internal sealed class SharedIndentedBuilder
{
    private readonly StringBuilder _builder = new();
    private readonly CancellationToken _cancellationToken;
    private bool _atLineStart = true;

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

        if (!_atLineStart)
        {
            throw new InvalidOperationException(
                "Explicit indentation requires the start of a line."
            );
        }

        _builder.Append(' ', effective * 4);
        _atLineStart = false;
        return this;
    }

    public SharedIndentedBuilder Append(string value)
    {
        var start = 0;
        while (start < value.Length)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var newline = value.IndexOf('\n', start);
            var end = newline < 0 ? value.Length : newline;
            if (end > start)
            {
                if (_atLineStart)
                {
                    AppendIndent(0);
                }
                _builder.Append(value, start, end - start);
            }
            if (newline < 0)
            {
                break;
            }
            _builder.Append('\n');
            _atLineStart = true;
            start = newline + 1;
        }
        return this;
    }

    public SharedIndentedBuilder Append(char value)
    {
        if (value != '\n' && _atLineStart)
        {
            AppendIndent(0);
        }
        _builder.Append(value);
        _atLineStart = value == '\n';
        return this;
    }

    public SharedIndentedBuilder Append(int value)
    {
        Append(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    public SharedIndentedBuilder AppendLine(string value = "")
    {
        Append(value);
        Append('\n');
        return this;
    }

    public SharedIndentedBuilder AppendLineAt(int level, string value)
    {
        if (value.Length != 0)
        {
            AppendIndent(level);
        }
        return AppendLine(value);
    }

    public override string ToString()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return _builder.ToString();
    }
}
