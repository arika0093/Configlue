namespace Configlue.Provider.Yaml;

/// <summary>
/// Lexical rules shared by the YAML flow scanners.
/// </summary>
/// <remarks>
/// A quote character only opens a quoted scalar where a new scalar can start
/// (after a separation space, a line break, or a flow separator). An apostrophe
/// inside a plain scalar such as <c>can't</c> is part of the scalar.
/// A hash only opens a comment where a comment can start: at the beginning of
/// the scanned text, after whitespace, or right after a flow separator
/// (<c>,</c>, <c>[</c>, <c>{</c>). A hash inside a plain scalar such as
/// <c>a#b</c> (or <c>a:#b</c>) is part of the scalar.
/// </remarks>
internal static class YamlFlowScanning
{
    internal static bool IsQuoteStart(string source, int position)
    {
        if (position <= 0)
        {
            return true;
        }

        var previous = source[position - 1];
        return previous is ' ' or '\t' or '\r' or '\n' or ',' or '[' or '{' or ':';
    }

    internal static bool IsQuoteStart(ReadOnlySpan<char> line, int position)
    {
        if (position <= 0)
        {
            return true;
        }

        var previous = line[position - 1];
        return previous is ' ' or '\t' or '\r' or '\n' or ',' or '[' or '{' or ':';
    }

    internal static bool IsCommentStart(string source, int position)
    {
        if (position <= 0)
        {
            return true;
        }

        var previous = source[position - 1];
        return previous is ' ' or '\t' or '\r' or '\n' or ',' or '[' or '{';
    }

    internal static bool IsCommentStart(ReadOnlySpan<char> line, int position)
    {
        if (position <= 0)
        {
            return true;
        }

        var previous = line[position - 1];
        return previous is ' ' or '\t' or '\r' or '\n' or ',' or '[' or '{';
    }
}
