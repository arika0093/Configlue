namespace Configlue.Resource.Vault;

/// <summary>The bytes and version metadata of one Vault KV secret.</summary>
/// <remarks>
/// A <see langword="null"/> secret from <see cref="IVaultKvClient"/> means the path is
/// missing, deleted, or destroyed; all three map to a normal Configlue missing-resource
/// result. Secret content is never exposed through diagnostics.
/// </remarks>
public sealed record VaultKvSecret
{
    /// <summary>Creates a Vault KV secret.</summary>
    public VaultKvSecret(ReadOnlyMemory<byte> content, long? version)
    {
        Content = content;
        Version = version;
    }

    /// <summary>The secret payload bytes.</summary>
    public ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>The KV v2 version, or <see langword="null"/> for KV v1 secrets.</summary>
    public long? Version { get; init; }
}

/// <summary>The version metadata of one Vault KV secret, without its payload.</summary>
/// <remarks>
/// Watchers poll this metadata so secret payloads are not re-downloaded when nothing changed.
/// </remarks>
public sealed record VaultKvSecretMetadata
{
    /// <summary>Creates Vault KV secret metadata.</summary>
    public VaultKvSecretMetadata(long? version)
    {
        Version = version;
    }

    /// <summary>The KV v2 version, or <see langword="null"/> for KV v1 secrets.</summary>
    public long? Version { get; init; }
}

/// <summary>The redacted failure of a Vault KV operation.</summary>
/// <remarks>
/// Messages never contain secret values or Vault tokens; they carry only non-sensitive
/// addressing such as the mount and path. Transports must preserve this guarantee and
/// must not embed secret material or tokens in <see cref="Exception.Message"/> either.
/// </remarks>
public class VaultKvException : Exception
{
    /// <summary>Creates a Vault KV exception.</summary>
    public VaultKvException(string message)
        : base(message) { }

    /// <summary>Creates a Vault KV exception with an inner cause.</summary>
    /// <remarks>The inner cause must not contain secret values or Vault tokens.</remarks>
    public VaultKvException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Raised when a Vault KV write targets a stale version.</summary>
public sealed class VaultKvConflictException : VaultKvException
{
    /// <summary>Creates a Vault KV conflict exception.</summary>
    public VaultKvConflictException(string message)
        : base(message) { }

    /// <summary>Creates a Vault KV conflict exception with an inner cause.</summary>
    public VaultKvConflictException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Raised for a transient Vault server failure that callers may retry.</summary>
/// <remarks>
/// Resource reads surface this error instead of hiding it. Change watchers treat it as
/// "unchanged for this poll" and retry on the next polling interval.
/// </remarks>
public sealed class VaultKvTransientException : VaultKvException
{
    /// <summary>Creates a Vault KV transient exception.</summary>
    public VaultKvTransientException(string message)
        : base(message) { }

    /// <summary>Creates a Vault KV transient exception with an inner cause.</summary>
    public VaultKvTransientException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Validates Vault mount and secret-path addressing.</summary>
/// <remarks>
/// Mounts and paths are explicit and slash-delimited (<c>secret/config/app</c> is addressed
/// as mount <c>secret</c> with path <c>config/app</c>). Empty segments, parent (<c>..</c>)
/// segments, leading slashes, and NUL characters are rejected so key mapping stays
/// deterministic across subjects and routes.
/// </remarks>
public static class VaultKvPath
{
    /// <summary>Validates a KV mount name.</summary>
    public static void ValidateMount(string mount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mount);
        ValidateSegments(mount, nameof(mount), allowSlashes: false);
    }

    /// <summary>Validates a KV secret path.</summary>
    public static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateSegments(path, nameof(path), allowSlashes: true);
    }

    private static void ValidateSegments(string value, string parameterName, bool allowSlashes)
    {
        if (value.Contains('\0'))
        {
            throw new ArgumentException("A Vault mount or path cannot contain NUL.", parameterName);
        }

        if (value.StartsWith("/", StringComparison.Ordinal) || value.Contains('\\'))
        {
            throw new ArgumentException(
                "A Vault mount or path must be relative and use '/' separators.",
                parameterName
            );
        }

        if (!allowSlashes && value.Contains('/'))
        {
            throw new ArgumentException(
                "A Vault mount is a single segment; put the remainder in the path.",
                parameterName
            );
        }

        foreach (var segment in value.Split('/'))
        {
            if (
                segment.Length == 0
                || string.Equals(segment, ".", StringComparison.Ordinal)
                || string.Equals(segment, "..", StringComparison.Ordinal)
            )
            {
                throw new ArgumentException(
                    "A Vault mount or path cannot contain empty, '.', or '..' segments.",
                    parameterName
                );
            }
        }
    }
}
