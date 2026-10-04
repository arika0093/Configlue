using System.Security.Cryptography;
using System.Text;

namespace Configlue.Source.Ssm;

/// <summary>
/// Deterministic conversion of Parameter Store names below a root path into
/// Configlue member paths.
/// </summary>
/// <remarks>
/// <para>Explicit naming rules:</para>
/// <list type="number">
/// <item><description>Root paths are normalized to start and end with <c>/</c> (for example <c>myapp/prod</c> becomes <c>/myapp/prod/</c>). Matching is ordinal and case-sensitive because Parameter Store paths are case-sensitive.</description></item>
/// <item><description>The relative name is the full name with the root prefix removed. Empty relatives (the root itself) and empty <c>//</c> segments are rejected as malformed payloads.</description></item>
/// <item><description>Relative names split on <c>/</c> into segments. Each segment resolves to one schema member case-insensitively (<see cref="StringComparison.OrdinalIgnoreCase"/>) using generated schema metadata; no runtime reflection traversal is used.</description></item>
/// <item><description>Segments additionally match when <c>-</c> and <c>_</c> are removed (for example <c>retry-count</c>, <c>retry_count</c>, and <c>RetryCount</c> all resolve to member <c>RetryCount</c>). A segment matching more than one member is ambiguous and rejected.</description></item>
/// <item><description>Deeper segments descend into nested models through generated <c>NestedSchemaFactory</c> metadata. Continuing past a non-nested member, naming a nested model without a leaf, or referencing an unknown member path is handled explicitly: unknown leaf paths are ignored so unrelated parameters can share a root; structural misuse is an invalid payload.</description></item>
/// <item><description>Write mapping is the inverse: dotted member paths join with <c>/</c> below the normalized root using the generated member names verbatim.</description></item>
/// </list>
/// </remarks>
internal static class SsmParameterPath
{
    public static string NormalizeRootPath(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (rootPath.Contains('\0'))
        {
            throw new ArgumentException(
                "A Parameter Store root path cannot contain NUL.",
                nameof(rootPath)
            );
        }

        var normalized = rootPath.Trim();
        if (normalized.Contains(' ', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A Parameter Store root path cannot contain spaces.",
                nameof(rootPath)
            );
        }

        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        if (!normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        if (normalized.Contains("//", StringComparison.Ordinal) && normalized.Length > 1)
        {
            throw new ArgumentException(
                $"Parameter Store root path '{RedactRoot(rootPath)}' contains an empty segment.",
                nameof(rootPath)
            );
        }

        return normalized;
    }

    public static string RedactRoot(string rootPath) => rootPath;

    public static bool TryGetRelative(
        string rootPath,
        string fullName,
        out string relative,
        out string? failure
    )
    {
        if (!fullName.StartsWith(rootPath, StringComparison.Ordinal))
        {
            relative = string.Empty;
            failure = $"Parameter '{fullName}' is outside the root path.";
            return false;
        }

        relative = fullName.Substring(rootPath.Length);
        if (relative.Length == 0)
        {
            failure = $"Parameter '{fullName}' names the root path itself.";
            relative = string.Empty;
            return false;
        }

        if (relative.StartsWith("/", StringComparison.Ordinal))
        {
            relative = relative.TrimStart('/');
        }

        if (relative.Length == 0)
        {
            failure = $"Parameter '{fullName}' names the root path itself.";
            return false;
        }

        failure = null;
        return true;
    }

    public static string[] SplitRelative(string relative)
    {
        return relative.Split('/');
    }

    public static string NormalizeSegment(string segment)
    {
        var builder = new StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            if (ch is '-' or '_')
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }

    public static string CreateRevision(IReadOnlyList<SsmParameterData> parameters)
    {
        var ordered = parameters.OrderBy(static p => p.Name, StringComparer.Ordinal).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var parameter in ordered)
        {
            AppendString(hash, parameter.Name);
            AppendString(
                hash,
                parameter.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            AppendString(hash, parameter.Type ?? string.Empty);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
#if NETSTANDARD2_0
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = BitConverter.GetBytes(bytes.Length);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(length);
        }

        hash.AppendData(length);
        hash.AppendData(bytes);
#else
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> lengthPrefix = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, byteCount);
        hash.AppendData(lengthPrefix);
        if (byteCount == 0)
        {
            return;
        }

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, 0);
            hash.AppendData(new ReadOnlySpan<byte>(buffer, 0, written));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
#endif
    }
}

/// <summary>
/// A case-insensitive index over one schema's members that distinguishes a unique
/// match from an ambiguous one, so SSM path segments resolve through generated
/// metadata without rescanning members per segment.
/// </summary>
internal sealed class SsmMemberLookup
{
    private readonly Dictionary<string, ConfiglueMemberSchema> _byName;
    private readonly Dictionary<string, List<ConfiglueMemberSchema>> _byNormalized;
    private readonly HashSet<string> _ambiguous;

    private SsmMemberLookup(
        Dictionary<string, ConfiglueMemberSchema> byName,
        Dictionary<string, List<ConfiglueMemberSchema>> byNormalized,
        HashSet<string> ambiguous
    )
    {
        _byName = byName;
        _byNormalized = byNormalized;
        _ambiguous = ambiguous;
    }

    public static SsmMemberLookup Create(ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var byName = new Dictionary<string, ConfiglueMemberSchema>(
            StringComparer.OrdinalIgnoreCase
        );
        var byNormalized = new Dictionary<string, List<ConfiglueMemberSchema>>(
            StringComparer.Ordinal
        );
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in schema.Members)
        {
            if (ambiguous.Contains(member.Name))
            {
                continue;
            }

            if (!byName.TryAdd(member.Name, member))
            {
                ambiguous.Add(member.Name);
                byName.Remove(member.Name);
            }

            var normalized = SsmParameterPath.NormalizeSegment(member.Name);
            if (!byNormalized.TryGetValue(normalized, out var matches))
            {
                matches = [];
                byNormalized.Add(normalized, matches);
            }

            matches.Add(member);
        }

        return new SsmMemberLookup(byName, byNormalized, ambiguous);
    }

    public bool TryResolve(string segment, out ConfiglueMemberSchema member, out bool isAmbiguous)
    {
        if (_ambiguous.Contains(segment))
        {
            member = default;
            isAmbiguous = true;
            return false;
        }

        if (_byName.TryGetValue(segment, out member))
        {
            isAmbiguous = false;
            return true;
        }

        var normalized = SsmParameterPath.NormalizeSegment(segment);
        if (_byNormalized.TryGetValue(normalized, out var matches))
        {
            if (matches.Count == 1)
            {
                member = matches[0];
                isAmbiguous = false;
                return true;
            }

            member = default;
            isAmbiguous = true;
            return false;
        }

        member = default;
        isAmbiguous = false;
        return false;
    }
}
