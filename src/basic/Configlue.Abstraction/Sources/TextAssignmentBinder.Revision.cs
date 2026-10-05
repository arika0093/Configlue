using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Configlue.Sources;

internal static partial class TextAssignmentBinder
{
    /// <summary>Renders a converted or raw value for deterministic revision hashing.</summary>
    /// <remarks>
    /// Rendering is logical, not concrete: sequences render as
    /// "[element,...]" and dictionaries as "{key=value,...}" without the
    /// concrete collection type name, so a JSON-deserialized
    /// <c>List&lt;T&gt;</c> and a typed <c>T[]</c> with equal elements hash
    /// equally. Scalar rendering keeps the concrete type name.
    /// </remarks>
    internal static string Render(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var type = value.GetType();
        if (value is string text)
        {
            return $"{type.FullName}:\"{text}\"";
        }

        if (value is bool boolean)
        {
            return boolean ? $"{type.FullName}:true" : $"{type.FullName}:false";
        }

        if (value is char character)
        {
            return $"{type.FullName}:{character}";
        }

        if (value is DateTime dateTime)
        {
            return $"{type.FullName}:{dateTime.ToString("O", CultureInfo.InvariantCulture)}";
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            return $"{type.FullName}:{dateTimeOffset.ToString("O", CultureInfo.InvariantCulture)}";
        }

#if !NETSTANDARD
        if (value is DateOnly dateOnly)
        {
            return $"{type.FullName}:{dateOnly.ToString("O", CultureInfo.InvariantCulture)}";
        }

        if (value is TimeOnly timeOnly)
        {
            return $"{type.FullName}:{timeOnly.ToString("O", CultureInfo.InvariantCulture)}";
        }
#else
        if (
            (type.FullName == "System.DateOnly" || type.FullName == "System.TimeOnly")
            && value is IFormattable dateTimeText
        )
        {
            return $"{type.FullName}:{dateTimeText.ToString("O", CultureInfo.InvariantCulture)}";
        }
#endif

        if (value is TimeSpan timeSpan)
        {
            return $"{type.FullName}:{timeSpan.ToString("c", CultureInfo.InvariantCulture)}";
        }

        if (value is Guid guid)
        {
            return $"{type.FullName}:{guid:D}";
        }

        if (value is byte[] bytes)
        {
            return $"{type.FullName}:{System.Convert.ToBase64String(bytes)}";
        }

        if (value is IDictionary dictionary)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            var first = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(Render(entry.Key)).Append('=').Append(Render(entry.Value));
            }

            builder.Append('}');
            return builder.ToString();
        }

        if (value is IEnumerable sequence)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            var first = true;
            foreach (var item in sequence)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(Render(item));
            }

            builder.Append(']');
            return builder.ToString();
        }

        if (value is IConvertible convertible)
        {
            return $"{type.FullName}:{System.Convert.ToString(convertible, CultureInfo.InvariantCulture)}";
        }

        return $"{type.FullName}:{value}";
    }

    private static string CreateRevision(
        IReadOnlyDictionary<string, object?> converted,
        IReadOnlyList<TextAssignment> unmatched,
        CancellationToken cancellationToken
    )
    {
        var keys = new List<string>(converted.Keys);
        keys.Sort(StringComparer.Ordinal);
        var unmatchedKeys = new List<string>();
        foreach (var item in unmatched)
        {
            unmatchedKeys.Add(item.Origin);
        }

        unmatchedKeys.Sort(StringComparer.Ordinal);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendHashedString(hash, path);
            AppendHashedString(hash, Render(converted[path]));
        }

        foreach (var origin in unmatchedKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Include unmatched origins so transports observe underlying changes
            // even when no member is bound.
            var raw = unmatched
                .First(item => string.Equals(item.Origin, origin, StringComparison.Ordinal))
                .RawValue;
            AppendHashedString(hash, origin);
            AppendHashedString(hash, Render(raw));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendHashedString(IncrementalHash hash, string value)
    {
        Span<byte> prefix = stackalloc byte[12];
#if NETSTANDARD
        var prefixLength = WriteLengthPrefix(prefix, value.Length);
        hash.AppendData(prefix[..prefixLength].ToArray());
        hash.AppendData(Encoding.UTF8.GetBytes(value));
#else
        var prefixLength = WriteLengthPrefix(prefix, value.Length);
        hash.AppendData(prefix[..prefixLength]);
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount == 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(value, buffer);
            hash.AppendData(buffer.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
#endif
    }

    private static int WriteLengthPrefix(Span<byte> buffer, int length)
    {
        var digits = 0;
        var remaining = length;
        do
        {
            digits++;
            remaining /= 10;
        } while (remaining != 0);

        var index = digits;
        remaining = length;
        do
        {
            index--;
            buffer[index] = (byte)('0' + (remaining % 10));
            remaining /= 10;
        } while (remaining != 0);

        buffer[digits] = (byte)':';
        return digits + 1;
    }
}
