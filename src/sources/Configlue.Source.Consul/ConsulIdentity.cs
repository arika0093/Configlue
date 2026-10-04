using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Configlue.Source.Consul;

internal static class ConsulIdentityHash
{
    public static string Create(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBuffer = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            lengthBuffer[0] = (byte)(bytes.Length >> 24);
            lengthBuffer[1] = (byte)(bytes.Length >> 16);
            lengthBuffer[2] = (byte)(bytes.Length >> 8);
            lengthBuffer[3] = (byte)bytes.Length;
#if NETSTANDARD
            hash.AppendData(lengthBuffer.ToArray());
            hash.AppendData(bytes);
#else
            hash.AppendData(lengthBuffer);
            hash.AppendData(bytes);
#endif
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

internal static class ConsulKeyNormalization
{
    public static string NormalizePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var normalized = prefix.Trim().Trim('/');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A Consul key prefix cannot be empty.", nameof(prefix));
        }

        if (normalized.Contains('\0') || normalized.Contains('\n'))
        {
            throw new ArgumentException(
                "A Consul key prefix cannot contain NUL or newline characters.",
                nameof(prefix)
            );
        }

        return normalized;
    }

    public static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = key.Trim().Trim('/');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A Consul key cannot be empty.", nameof(key));
        }

        return normalized;
    }

    public static string FormatRevision(ulong index) =>
        index.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseRevision(string? revision, out ulong index)
    {
        index = 0;
        return revision is not null
            && ulong.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }
}
