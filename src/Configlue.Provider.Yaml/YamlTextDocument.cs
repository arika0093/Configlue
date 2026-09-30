using SharpYaml;
using SharpYaml.Model;

namespace Configlue.Provider.Yaml;

internal sealed class YamlTextDocument
{
    private YamlTextDocument(string source, YamlTextNode root)
    {
        Source = source;
        Root = root;
    }

    internal string Source { get; }

    internal YamlTextNode Root { get; }

    internal YamlTextNode? GetPath(IReadOnlyList<string> path)
    {
        var current = Root;
        foreach (var segment in path)
        {
            if (current.Kind != YamlTextKind.Mapping)
            {
                throw new YamlException(
                    $"YAML section path '{string.Join(":", path)}' crosses a non-mapping value at '{segment}'."
                );
            }

            var property = current.Properties!.SingleOrDefault(candidate =>
                string.Equals(candidate.Name, segment, StringComparison.Ordinal)
            );
            if (property is null)
            {
                return null;
            }

            current = property.Value;
        }

        return current;
    }

    internal static YamlTextDocument Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new YamlTextDocument(
                source,
                new YamlTextNode(YamlTextKind.Mapping, new YamlMapping(), source, 0, 0)
            );
        }

        using var reader = new StringReader(source);
        var stream = YamlStream.Load(reader, null);
        if (stream.Count != 1)
        {
            throw new YamlException("A Configlue YAML resource must contain exactly one document.");
        }

        YamlElement root =
            stream[0].Contents ?? throw new YamlException("A YAML document cannot be empty.");
        return new YamlTextDocument(source, BuildNode(root, source));
    }

    private static YamlTextNode BuildNode(YamlElement element, string source)
    {
        var events = element.EnumerateEvents().ToArray();
        if (events.Length == 0)
        {
            throw new YamlException("A YAML node has no source events.");
        }

        var start = events[0].Start.Index;
        var end = events[^1].End.Index;
        if (element is YamlValue scalar)
        {
            start = scalar.Scalar.Start.Index;
            end = scalar.Scalar.End.Index;
            return new YamlTextNode(YamlTextKind.Scalar, element, source, start, end);
        }

        if (element is YamlMapping mapping)
        {
            if (mapping.Style == YamlStyle.Flow)
            {
                end = FindFlowClosing(source, start, '}') + 1;
            }

            var node = new YamlTextNode(YamlTextKind.Mapping, element, source, start, end);
            foreach (var pair in mapping)
            {
                if (pair.Key is not YamlValue key)
                {
                    throw new YamlException("Structured YAML updates require scalar mapping keys.");
                }

                var value = BuildNode(
                    pair.Value ?? throw new YamlException("A YAML mapping value cannot be null."),
                    source
                );
                var entryStart = FindLineStart(source, key.Scalar.Start.Index);
                var entryEnd = GetEntryEnd(value, source);
                var valueStart =
                    value.Kind == YamlTextKind.Scalar
                        ? value.Start
                        : FindValueStart(source, key.Scalar.End.Index);
                value.SetEntry(entryStart, entryEnd, valueStart, value.End);
                node.Properties!.Add(
                    new YamlTextProperty(
                        key.Value,
                        key.Scalar.Start.Index,
                        key.Scalar.End.Index,
                        entryStart,
                        entryEnd,
                        valueStart,
                        value.End,
                        value
                    )
                );
            }

            return node;
        }

        if (element is YamlSequence sequence)
        {
            if (sequence.Style == YamlStyle.Flow)
            {
                end = FindFlowClosing(source, start, ']') + 1;
            }

            var node = new YamlTextNode(YamlTextKind.Sequence, element, source, start, end);
            foreach (var child in sequence)
            {
                var item = BuildNode(child, source);
                var entryStart = FindLineStart(source, item.Start);
                item.SetEntry(entryStart, GetEntryEnd(item, source), item.Start, item.End);
                node.Items!.Add(item);
            }

            return node;
        }

        throw new YamlException($"Unsupported YAML node type '{element.GetType()}'.");
    }

    private static int FindLineStart(string source, int position)
    {
        while (position > 0 && source[position - 1] is not '\r' and not '\n')
        {
            position--;
        }

        return position;
    }

    private static int FindValueStart(string source, int keyEnd)
    {
        var position = keyEnd;
        while (position < source.Length && source[position] is not '\r' and not '\n')
        {
            if (source[position] == ':')
            {
                return position + 1;
            }

            position++;
        }

        return keyEnd;
    }

    private static int GetEntryEnd(YamlTextNode node, string source)
    {
        if (
            (
                node.Kind == YamlTextKind.Mapping
                && ((YamlMapping)node.Element).Style == YamlStyle.Block
            )
            || (
                node.Kind == YamlTextKind.Sequence
                && ((YamlSequence)node.Element).Style == YamlStyle.Block
            )
        )
        {
            return node.End;
        }

        var end = Math.Max(0, Math.Min(node.End, source.Length));
        while (end < source.Length && source[end] is not '\r' and not '\n')
        {
            end++;
        }

        if (end < source.Length && source[end] == '\r')
        {
            end++;
        }

        if (end < source.Length && source[end] == '\n')
        {
            end++;
        }

        return end;
    }

    private static int FindFlowClosing(string source, int start, char expectedClosing)
    {
        var depth = 0;
        var singleQuoted = false;
        var doubleQuoted = false;
        var position = start;
        while (position < source.Length)
        {
            var character = source[position];
            if (doubleQuoted)
            {
                if (character == '\\')
                {
                    position += 2;
                    continue;
                }

                if (character == '"')
                {
                    doubleQuoted = false;
                }
            }
            else if (singleQuoted)
            {
                if (
                    character == '\''
                    && position + 1 < source.Length
                    && source[position + 1] == '\''
                )
                {
                    position += 2;
                    continue;
                }

                if (character == '\'')
                {
                    singleQuoted = false;
                }
            }
            else if (character == '"')
            {
                doubleQuoted = true;
            }
            else if (character == '\'')
            {
                singleQuoted = true;
            }
            else if (character == '#')
            {
                while (position < source.Length && source[position] is not '\r' and not '\n')
                {
                    position++;
                }

                continue;
            }
            else if (character is '[' or '{')
            {
                depth++;
            }
            else if (character is ']' or '}')
            {
                depth--;
                if (depth == 0 && character == expectedClosing)
                {
                    return position;
                }
            }

            position++;
        }

        throw new YamlException("The YAML flow collection has no closing delimiter.");
    }
}

internal enum YamlTextKind
{
    Scalar,
    Mapping,
    Sequence,
}

internal sealed class YamlTextNode(
    YamlTextKind kind,
    YamlElement element,
    string source,
    int start,
    int end
)
{
    internal YamlTextKind Kind { get; } = kind;

    internal YamlElement Element { get; } = element;

    internal string Source { get; } = source;

    internal int Start { get; } = start;

    internal int End { get; } = end;

    internal int EntryStart { get; private set; } = start;

    internal int EntryEnd { get; private set; } = end;

    internal int ValueStart { get; private set; } = start;

    internal int ValueEnd { get; private set; } = end;

    internal List<YamlTextProperty>? Properties { get; } = kind == YamlTextKind.Mapping ? [] : null;

    internal List<YamlTextNode>? Items { get; } = kind == YamlTextKind.Sequence ? [] : null;

    internal void SetEntry(int entryStart, int entryEnd, int valueStart, int valueEnd)
    {
        EntryStart = entryStart;
        EntryEnd = entryEnd;
        ValueStart = valueStart;
        ValueEnd = valueEnd;
    }
}

internal sealed class YamlTextProperty(
    string name,
    int keyStart,
    int keyEnd,
    int entryStart,
    int entryEnd,
    int valueStart,
    int valueEnd,
    YamlTextNode value
)
{
    internal string Name { get; } = name;

    internal int KeyStart { get; } = keyStart;

    internal int KeyEnd { get; } = keyEnd;

    internal int EntryStart { get; } = entryStart;

    internal int EntryEnd { get; } = entryEnd;

    internal int ValueStart { get; } = valueStart;

    internal int ValueEnd { get; } = valueEnd;

    internal YamlTextNode Value { get; } = value;
}
