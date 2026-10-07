using System.Buffers;
using System.ComponentModel;
using System.Text;

namespace Configlue;

/// <summary>RFC 6901 JSON Pointer parsing and escaping.</summary>
/// <remarks>
/// Product-neutral parsing core mirrored from
/// <c>src/fragments:src/SparseFragments/JsonPatch/JsonPointer.cs</c>
/// (namespace <c>SparseFragments</c>). The public <see cref="Parse"/>,
/// <see cref="Escape"/>, and <see cref="Format"/> shapes are the Configlue
/// adapter surface; per-pointer tokenization internals stay equivalent so a
/// future shared-source cutover is a namespace/visibility change only.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public static class JsonPointer
{
    /// <summary>Parses a pointer into unescaped reference tokens.</summary>
    /// <exception cref="JsonPatchException">When the pointer is malformed.</exception>
    public static string[] Parse(string pointer)
    {
        if (pointer is null)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MalformedPointer,
                "JSON Pointer must be a string."
            );
        }

        if (pointer.Length == 0)
        {
            return Array.Empty<string>();
        }

        if (pointer[0] != '/')
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MalformedPointer,
                $"JSON Pointer '{pointer}' must be empty or start with '/'."
            );
        }

        // Size the token array by counting segments so a single pass tokenizes
        // the pointer without an intermediate Substring/Split allocation.
        var count = 1;
        for (var scan = 1; scan < pointer.Length; scan++)
        {
            if (pointer[scan] == '/')
            {
                count++;
            }
        }

        var tokens = new string[count];
        var tokenIndex = 0;
        var segmentStart = 1;
        for (var scan = 1; scan <= pointer.Length; scan++)
        {
            if (scan == pointer.Length || pointer[scan] == '/')
            {
                tokens[tokenIndex++] = Unescape(pointer, segmentStart, scan - segmentStart);
                segmentStart = scan + 1;
            }
        }

        return tokens;
    }

    /// <summary>Escapes one reference token for use in a pointer.</summary>
    public static string Escape(string token)
    {
        if (token is null)
        {
            return string.Empty;
        }

        return token.Replace("~", "~0").Replace("/", "~1");
    }

    /// <summary>Converts tokens back to pointer syntax.</summary>
    public static string Format(string[] tokens)
    {
        if (tokens is null || tokens.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var token in tokens)
        {
            builder.Append('/');
            builder.Append(Escape(token));
        }

        return builder.ToString();
    }

    private static string Unescape(string pointer, int start, int length)
    {
        var firstEscape = pointer.IndexOf('~', start, length);
        if (firstEscape < 0)
        {
            return pointer.Substring(start, length);
        }

        // Decoding only shortens the token. Keep small temporary buffers on the
        // stack and rent a reusable buffer for longer tokens.
        var rented = length > 256 ? ArrayPool<char>.Shared.Rent(length) : null;
        Span<char> buffer = rented is null ? stackalloc char[length] : rented;
        try
        {
            var written = firstEscape - start;
            pointer.AsSpan(start, written).CopyTo(buffer);
            var end = start + length;
            var index = firstEscape;
            while (index < end)
            {
                var c = pointer[index];
                if (c != '~')
                {
                    buffer[written++] = c;
                    index++;
                    continue;
                }

                if (index + 1 >= end)
                {
                    throw new JsonPatchException(
                        JsonPatchErrorKind.MalformedPointer,
                        $"JSON Pointer '{pointer}' has a dangling '~' escape."
                    );
                }

                var next = pointer[index + 1];
                if (next == '0')
                {
                    buffer[written++] = '~';
                }
                else if (next == '1')
                {
                    buffer[written++] = '/';
                }
                else
                {
                    throw new JsonPatchException(
                        JsonPatchErrorKind.MalformedPointer,
                        $"JSON Pointer '{pointer}' has an invalid '~' escape."
                    );
                }

                index += 2;
            }

            return buffer.Slice(0, written).ToString();
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }
}
