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
        IReadOnlyList<KeyValuePair<string, object?>> converted,
        IReadOnlyList<TextAssignment> unmatched,
        CancellationToken cancellationToken
    )
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Bind already applies unique canonical paths in ordinal order.
        for (var index = 0; index < converted.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = converted[index];
            AppendHashedString(hash, entry.Key);
            AppendHashedString(hash, Render(entry.Value));
        }

        if (unmatched.Count == 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendHashedString(hash, unmatched[0].Origin);
            AppendHashedString(hash, Render(unmatched[0].RawValue));
        }
        else if (unmatched.Count > 1)
        {
            var indices = new int[unmatched.Count];
            for (var index = 0; index < indices.Length; index++)
            {
                indices[index] = index;
            }

            Array.Sort(indices, new UnmatchedAssignmentComparer(unmatched));

            string? previousOrigin = null;
            object? firstRawValue = null;
            foreach (var index in indices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var assignment = unmatched[index];
                if (!string.Equals(previousOrigin, assignment.Origin, StringComparison.Ordinal))
                {
                    previousOrigin = assignment.Origin;
                    firstRawValue = assignment.RawValue;
                }

                // Equal origins retain their first input value, once per occurrence.
                AppendHashedString(hash, assignment.Origin);
                AppendHashedString(hash, Render(firstRawValue));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class UnmatchedAssignmentComparer : IComparer<int>
    {
        private readonly IReadOnlyList<TextAssignment> _assignments;

        public UnmatchedAssignmentComparer(IReadOnlyList<TextAssignment> assignments) =>
            _assignments = assignments;

        public int Compare(int left, int right)
        {
            var compared = StringComparer.Ordinal.Compare(
                _assignments[left].Origin,
                _assignments[right].Origin
            );
            return compared != 0 ? compared : left.CompareTo(right);
        }
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
