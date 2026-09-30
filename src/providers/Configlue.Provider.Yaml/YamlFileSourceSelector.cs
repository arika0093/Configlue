using System.Security.Cryptography;
using System.Text;

namespace Configlue.Provider.Yaml;

/// <summary>A semantic selector for a YAML file source and optional document section.</summary>
public sealed record YamlFileSourceSelector
{
    internal YamlFileSourceSelector(string sourceId)
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
        var identity = $"configlue-yaml-file-v1\n{canonicalPath}\n{canonicalSection}";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"yaml-file:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

/// <summary>Creates semantic selectors for YAML files.</summary>
public static class YamlFileSource
{
    /// <summary>Selects a YAML file source by path and optional document section.</summary>
    public static YamlFileSourceSelector At(string path, string? sectionPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (sectionPath is not null && string.IsNullOrWhiteSpace(sectionPath))
        {
            throw new ArgumentException("A section path cannot be empty.", nameof(sectionPath));
        }

        return new YamlFileSourceSelector(YamlFileSourceSelector.CreateSourceId(path, sectionPath));
    }
}

/// <summary>Gets write handles for YAML file sources selected by path.</summary>
public static class YamlFileSourceSelectorExtensions
{
    /// <summary>Gets a write handle for the selected YAML file source.</summary>
    public static ConfiglueSourceHandle<TModel> Source<TModel>(
        this IConfiglueSources<TModel> options,
        YamlFileSourceSelector selector
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selector);
        return options.Source(SourceKey<TModel>.Named(selector.SourceId));
    }
}
