using System.Security.Cryptography;
using System.Text;

namespace Configlue.Sources;

internal static class StateSourceIdentity
{
    public static string Create<T>(
        ISourceReader<T> reader,
        string? physicalOrigin,
        ResourceId? resourceId,
        string? logicalDescriptor
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        var readerType = reader.GetType();
        var kindType = readerType.IsConstructedGenericType
            ? readerType.GetGenericTypeDefinition()
            : readerType;
        var kind = kindType.FullName ?? kindType.Name;
        var locator = resourceId?.Value ?? physicalOrigin;
        var descriptor = logicalDescriptor?.Trim().Normalize(NormalizationForm.FormKC);
        if (string.IsNullOrWhiteSpace(descriptor) && string.IsNullOrWhiteSpace(locator))
        {
            return $"auto:{Guid.NewGuid():N}";
        }

        var canonical = string.Join(
            "\n",
            "configlue-source-v1",
            kind.Normalize(NormalizationForm.FormKC),
            locator?.Trim().Normalize(NormalizationForm.FormKC) ?? string.Empty,
            descriptor ?? string.Empty
        );
#if NETSTANDARD
        using var algorithm = SHA256.Create();
        var hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        var hashText = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
#else
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var hashText = Convert.ToHexString(hash).ToLowerInvariant();
#endif
        return $"auto:{hashText}";
    }
}
