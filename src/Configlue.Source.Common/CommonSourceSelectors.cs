using Configlue;

namespace Configlue.Source.Common;

/// <summary>A semantic selector for one standard file layer in the common source preset.</summary>
public sealed record CommonSourceSelector
{
    internal CommonSourceSelector(string sourceId)
    {
        SourceId = sourceId;
    }

    internal string SourceId { get; init; }
}

/// <summary>Semantic selectors for the file layers registered by <see cref="CommonSourcePreset"/>.</summary>
public static class CommonSource
{
    /// <summary>Selects the global per-user file layer.</summary>
    public static CommonSourceSelector Global { get; } = new("common.global");

    /// <summary>Selects the local file layer.</summary>
    public static CommonSourceSelector Local { get; } = new("common.local");

    /// <summary>Selects the explicitly selected file layer.</summary>
    public static CommonSourceSelector Specific { get; } = new("common.specific");
}

/// <summary>Gets write handles for sources selected by Common's semantic selectors.</summary>
public static class CommonSourceSelectorExtensions
{
    /// <summary>Gets a write handle for one selected Common source layer.</summary>
    public static ConfiglueSourceHandle<TModel> Source<TModel>(
        this IConfiglueOptions<TModel> options,
        CommonSourceSelector selector
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selector);
        return options.Source(SourceKey<TModel>.FromId(selector.SourceId));
    }
}
