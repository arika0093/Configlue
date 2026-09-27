using System.Security.Cryptography;
using System.Text;
using Configlue;

namespace Configlue.Provider.Json;

/// <summary>A semantic selector for a JSON file source and optional document section.</summary>
public sealed record JsonFileSourceSelector
{
    internal JsonFileSourceSelector(string sourceId)
    {
        SourceId = sourceId;
    }

    internal string SourceId { get; init; }

    internal static string CreateSourceId(
        string path,
        string? sectionPath,
        string? mountPath = null
    )
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
        var canonicalMount = mountPath?.Trim().Normalize(NormalizationForm.FormKC) ?? string.Empty;
        var identity =
            $"configlue-json-file-v1\n{canonicalPath}\n{canonicalSection}\n{canonicalMount}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"json-file:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

/// <summary>Creates semantic selectors for JSON file sources.</summary>
public static class JsonFileSource
{
    /// <summary>Selects a JSON file source by path and optional JSON document section.</summary>
    public static JsonFileSourceSelector At(
        string path,
        string? sectionPath = null,
        string? mountPath = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (sectionPath is not null && string.IsNullOrWhiteSpace(sectionPath))
        {
            throw new ArgumentException("A section path cannot be empty.", nameof(sectionPath));
        }
        if (mountPath is not null && string.IsNullOrWhiteSpace(mountPath))
        {
            throw new ArgumentException("A mount path cannot be empty.", nameof(mountPath));
        }

        return new JsonFileSourceSelector(
            JsonFileSourceSelector.CreateSourceId(path, sectionPath, mountPath)
        );
    }
}

/// <summary>Gets write handles for JSON file sources selected by path.</summary>
public static class JsonFileSourceSelectorExtensions
{
    /// <summary>Gets a write handle for the selected JSON file source.</summary>
    public static ConfiglueSourceHandle<TModel> Source<TModel>(
        this IConfiglueOptions<TModel> options,
        JsonFileSourceSelector selector
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selector);
        return options.Source(SourceKey<TModel>.FromId(selector.SourceId));
    }
}
