using System.Security.Cryptography;
using System.Text;

namespace Configlue;

internal static class StateSourceIdentity
{
    public static string Create<T>(
        IStateReader<T> reader,
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
            '\n',
            "configlue-source-v1",
            kind.Normalize(NormalizationForm.FormKC),
            locator?.Trim().Normalize(NormalizationForm.FormKC) ?? string.Empty,
            descriptor ?? string.Empty
        );
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"auto:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
