using System.Text;

namespace Configlue.Resource.Etcd;

/// <summary>
/// Deterministic encoding between generated member paths and etcd keys.
/// Each member name becomes one <c>/</c>-separated segment; bytes outside the
/// unreserved set are percent-encoded so every member path has one stable key.
/// </summary>
public static class EtcdKeyEncoding
{
    /// <summary>Normalizes a configured prefix by trimming trailing separators.</summary>
    /// <param name="prefix">The configured key prefix.</param>
    public static string NormalizePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        EtcdResourceOptions.ValidatePrefix(prefix);
        var normalized = prefix.TrimEnd('/');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("An etcd key prefix must contain a name.", nameof(prefix));
        }

        return normalized;
    }

    /// <summary>Percent-encodes one key segment so member names map to stable keys.</summary>
    /// <param name="segment">The member name or subject part to encode.</param>
    public static string EscapeSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Length == 0)
        {
            throw new ArgumentException("An etcd key segment cannot be empty.", nameof(segment));
        }

        if (!segment.Any(static character => !IsUnreserved(character)))
        {
            return segment;
        }

        var builder = new StringBuilder(segment.Length + 8);
        var index = 0;
        while (index < segment.Length)
        {
            var character = segment[index];
            if (IsUnreserved(character))
            {
                builder.Append(character);
                index++;
                continue;
            }

            string source;
            if (
                char.IsHighSurrogate(character)
                && index + 1 < segment.Length
                && char.IsLowSurrogate(segment[index + 1])
            )
            {
                source = segment.Substring(index, 2);
                index += 2;
            }
            else
            {
                source = character.ToString();
                index++;
            }

            foreach (var encoded in Encoding.UTF8.GetBytes(source))
            {
                builder.Append('%');
                builder.Append("0123456789ABCDEF"[encoded >> 4]);
                builder.Append("0123456789ABCDEF"[encoded & 0xF]);
            }
        }

        return builder.ToString();
    }

    /// <summary>Decodes one percent-encoded key segment back to its member name.</summary>
    /// <param name="segment">The encoded key segment.</param>
    public static string UnescapeSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Length == 0)
        {
            throw new FormatException("An etcd key contains an empty path segment.");
        }

        if (segment.IndexOf('%') < 0)
        {
            return segment;
        }

        var bytes = new List<byte>(segment.Length);
        var index = 0;
        while (index < segment.Length)
        {
            var character = segment[index];
            if (character == '%')
            {
                if (index + 2 >= segment.Length)
                {
                    throw new FormatException(
                        $"An etcd key segment '{segment}' has a truncated escape."
                    );
                }

                bytes.Add(ParseHex(segment[index + 1], segment[index + 2], segment));
                index += 3;
                continue;
            }

            if (character > 127)
            {
                throw new FormatException($"An etcd key segment '{segment}' is not valid ASCII.");
            }

            bytes.Add((byte)character);
            index++;
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Joins encoded member names into the deterministic suffix for one member path.</summary>
    /// <param name="memberNames">The member names from the model root to the leaf.</param>
    public static string EncodeMemberSuffix(IReadOnlyList<string> memberNames)
    {
        ArgumentNullException.ThrowIfNull(memberNames);
        if (memberNames.Count == 0)
        {
            throw new ArgumentException(
                "A member path requires at least one segment.",
                nameof(memberNames)
            );
        }

        var builder = new StringBuilder();
        for (var index = 0; index < memberNames.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('/');
            }

            builder.Append(EscapeSegment(memberNames[index]));
        }

        return builder.ToString();
    }

    /// <summary>Builds the full etcd key for a member suffix under a prefix.</summary>
    /// <param name="prefix">The configured key prefix.</param>
    /// <param name="memberSuffix">The encoded member suffix.</param>
    public static string BuildKey(string prefix, string memberSuffix)
    {
        var normalized = NormalizePrefix(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberSuffix);
        return normalized + "/" + memberSuffix;
    }

    /// <summary>Builds the full etcd key for a subject-scoped member suffix under a prefix.</summary>
    /// <param name="prefix">The configured key prefix.</param>
    /// <param name="subjectPart">The subject part, empty for the default subject.</param>
    /// <param name="memberSuffix">The encoded member suffix.</param>
    public static string BuildKey(string prefix, string subjectPart, string memberSuffix)
    {
        var normalized = NormalizePrefix(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberSuffix);
        return string.IsNullOrEmpty(subjectPart)
            ? normalized + "/" + memberSuffix
            : normalized + "/" + EscapeSegment(subjectPart) + "/" + memberSuffix;
    }

    /// <summary>Splits a full etcd key into the member suffix below a prefix.</summary>
    /// <param name="prefix">The configured key prefix.</param>
    /// <param name="key">The full etcd key.</param>
    /// <param name="memberSuffix">Receives the suffix below the prefix when the key belongs to it.</param>
    public static bool TrySplitMemberSuffix(string prefix, string key, out string memberSuffix)
    {
        var normalized = NormalizePrefix(prefix);
        if (key.Equals(normalized, StringComparison.Ordinal))
        {
            memberSuffix = string.Empty;
            return false;
        }

        if (!key.StartsWith(normalized + "/", StringComparison.Ordinal))
        {
            memberSuffix = string.Empty;
            return false;
        }

        memberSuffix = key.Substring(normalized.Length + 1);
        return memberSuffix.Length > 0;
    }

    /// <summary>Decodes an encoded member suffix back to its member names.</summary>
    /// <param name="memberSuffix">The encoded member suffix.</param>
    public static string[] DecodeMemberSuffix(string memberSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberSuffix);
        var segments = memberSuffix.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = UnescapeSegment(segments[index]);
        }

        return segments;
    }

    internal static bool IsUnreserved(char character) =>
        (character >= 'a' && character <= 'z')
        || (character >= 'A' && character <= 'Z')
        || (character >= '0' && character <= '9')
        || character is '-' or '_' or '.' or '~';

    private static byte ParseHex(char high, char low, string segment)
    {
        return (byte)((HexValue(high, segment) << 4) | HexValue(low, segment));
    }

    private static int HexValue(char character, string segment)
    {
        if (character >= '0' && character <= '9')
        {
            return character - '0';
        }

        if (character >= 'A' && character <= 'F')
        {
            return character - 'A' + 10;
        }

        if (character >= 'a' && character <= 'f')
        {
            return character - 'a' + 10;
        }

        throw new FormatException($"An etcd key segment '{segment}' has an invalid escape.");
    }
}

/// <summary>
/// Encodes the Configlue revision for one etcd prefix contribution as
/// <c>{headerRevision}:{escapedKey}={modRevision};…</c> with keys sorted for determinism.
/// The per-key modification revisions make the token sufficient to detect a stale
/// baseline and to build compare-and-swap preconditions for writes.
/// </summary>
public static class EtcdRevisionCodec
{
    /// <summary>Encodes a Configlue revision from a header revision and per-key modifications.</summary>
    /// <param name="headerRevision">The etcd cluster revision of the read.</param>
    /// <param name="keyRevisions">The modification revision of each contributing key.</param>
    public static string Encode(long headerRevision, IReadOnlyDictionary<string, long> keyRevisions)
    {
        if (headerRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(headerRevision));
        }

        ArgumentNullException.ThrowIfNull(keyRevisions);
        if (keyRevisions.Count == 0)
        {
            return headerRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
        }

        var keys = new List<string>(keyRevisions.Keys);
        keys.Sort(StringComparer.Ordinal);
        var builder = new StringBuilder();
        builder.Append(headerRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(':');
        for (var index = 0; index < keys.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(';');
            }

            var modRevision = keyRevisions[keys[index]];
            if (modRevision <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(keyRevisions));
            }

            builder.Append(EscapeRevisionKey(keys[index]));
            builder.Append('=');
            builder.Append(modRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>Decodes a Configlue revision into its header and per-key revisions.</summary>
    /// <param name="revision">The revision token to decode.</param>
    /// <param name="headerRevision">Receives the etcd cluster revision of the read.</param>
    /// <param name="keyRevisions">Receives the modification revision of each contributing key.</param>
    public static bool TryDecode(
        string? revision,
        out long headerRevision,
        out Dictionary<string, long> keyRevisions
    )
    {
        headerRevision = 0;
        keyRevisions = new Dictionary<string, long>(StringComparer.Ordinal);
        if (revision is null || revision.Length == 0)
        {
            return false;
        }

        var separator = revision.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        if (
            !long.TryParse(
                revision.Substring(0, separator),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out headerRevision
            )
            || headerRevision <= 0
        )
        {
            headerRevision = 0;
            return false;
        }

        var remainder = revision.Substring(separator + 1);
        if (remainder.Length == 0)
        {
            return true;
        }

        foreach (var entry in remainder.Split(';'))
        {
            var equals = entry.IndexOf('=');
            if (equals <= 0 || equals == entry.Length - 1)
            {
                return false;
            }

            string key;
            try
            {
                key = UnescapeRevisionKey(entry.Substring(0, equals));
            }
            catch (FormatException)
            {
                return false;
            }

            if (
                !long.TryParse(
                    entry.Substring(equals + 1),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var modRevision
                )
                || modRevision <= 0
                || !keyRevisions.TryAdd(key, modRevision)
            )
            {
                return false;
            }
        }

        return true;
    }

    internal static string EscapeRevisionKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        var index = 0;
        while (index < key.Length)
        {
            var character = key[index];
            if (EtcdKeyEncoding.IsUnreserved(character) || character == '/')
            {
                builder.Append(character);
                index++;
                continue;
            }

            string source;
            if (
                char.IsHighSurrogate(character)
                && index + 1 < key.Length
                && char.IsLowSurrogate(key[index + 1])
            )
            {
                source = key.Substring(index, 2);
                index += 2;
            }
            else
            {
                source = character.ToString();
                index++;
            }

            foreach (var encoded in Encoding.UTF8.GetBytes(source))
            {
                builder.Append('%');
                builder.Append("0123456789ABCDEF"[encoded >> 4]);
                builder.Append("0123456789ABCDEF"[encoded & 0xF]);
            }
        }

        return builder.ToString();
    }

    internal static string UnescapeRevisionKey(string key)
    {
        if (key.IndexOf('%') < 0)
        {
            return key;
        }

        var bytes = new List<byte>(key.Length);
        var index = 0;
        while (index < key.Length)
        {
            var character = key[index];
            if (character != '%')
            {
                if (character > 127)
                {
                    throw new FormatException($"An etcd revision key '{key}' is not valid ASCII.");
                }

                bytes.Add((byte)character);
                index++;
                continue;
            }

            if (index + 2 >= key.Length)
            {
                throw new FormatException($"An etcd revision key '{key}' has a truncated escape.");
            }

            bytes.Add(ParseRevisionHex(key[index + 1], key[index + 2], key));
            index += 3;
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static byte ParseRevisionHex(char high, char low, string key)
    {
        return (byte)((RevisionHexValue(high, key) << 4) | RevisionHexValue(low, key));
    }

    private static int RevisionHexValue(char character, string key)
    {
        if (character >= '0' && character <= '9')
        {
            return character - '0';
        }

        if (character >= 'A' && character <= 'F')
        {
            return character - 'A' + 10;
        }

        if (character >= 'a' && character <= 'f')
        {
            return character - 'a' + 10;
        }

        throw new FormatException($"An etcd revision key '{key}' has an invalid escape.");
    }
}
