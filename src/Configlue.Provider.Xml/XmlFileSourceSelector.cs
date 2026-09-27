using System.Security.Cryptography;
using System.Text;

namespace Configlue.Provider.Xml;

/// <summary>A semantic selector for an XML file source and optional document section.</summary>
public sealed record XmlFileSourceSelector
{
    internal XmlFileSourceSelector(string sourceId)
    {
        SourceId = sourceId;
    }

    internal string SourceId { get; init; }

    internal static string CreateSourceId(string path, string? sectionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            canonicalPath = canonicalPath.ToUpperInvariant();
        }

        canonicalPath = canonicalPath.Normalize(NormalizationForm.FormKC);
        var canonicalSection =
            sectionPath?.Trim().Normalize(NormalizationForm.FormKC) ?? string.Empty;
        var identity = $"configlue-xml-file-v1\n{canonicalPath}\n{canonicalSection}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"xml-file:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

/// <summary>Creates semantic selectors for XML files.</summary>
public static class XmlFileSource
{
    /// <summary>Selects an XML file source by path and optional element section.</summary>
    public static XmlFileSourceSelector At(string path, string? sectionPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (sectionPath is not null && string.IsNullOrWhiteSpace(sectionPath))
        {
            throw new ArgumentException("A section path cannot be empty.", nameof(sectionPath));
        }

        return new XmlFileSourceSelector(XmlFileSourceSelector.CreateSourceId(path, sectionPath));
    }
}

/// <summary>Gets write handles for XML file sources selected by path.</summary>
public static class XmlFileSourceSelectorExtensions
{
    /// <summary>Gets a write handle for the selected XML file source.</summary>
    public static ConfiglueSourceHandle<TModel> Source<TModel>(
        this IConfiglueOptions<TModel> options,
        XmlFileSourceSelector selector
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selector);
        return options.Source(SourceKey<TModel>.FromId(selector.SourceId));
    }
}
