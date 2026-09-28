using Configlue;

namespace Configlue.Source.Presets;

/// <summary>A semantic selector for one standard file layer in the common source preset.</summary>
public sealed record CommonSourceSelector
{
    internal CommonSourceSelector(string sourceId)
    {
        SourceId = sourceId;
    }

    // Keep the provider's routing identity opaque; callers select Common layers by role.
    internal string SourceId { get; init; }
}

/// <summary>Semantic selectors for the file layers registered by the common source preset.</summary>
public static class CommonSource
{
    /// <summary>Selects the global per-user file layer.</summary>
    public static CommonSourceSelector Global { get; } = new("f37ac095ae7e46a2bf8a0dfe16d56a55");

    /// <summary>Selects the local file layer.</summary>
    public static CommonSourceSelector Local { get; } = new("7af05177a67c4b02b52f3da9b052f782");

    /// <summary>Selects the explicitly selected file layer.</summary>
    public static CommonSourceSelector Specific { get; } = new("cc4bd195516a4269a527f254bcc7cd74");
}

/// <summary>Gets write handles for sources selected by Common's semantic selectors.</summary>
public static class CommonSourceSelectorExtensions
{
    /// <summary>Gets a write handle for one selected Common source layer.</summary>
    public static ConfiglueSourceHandle<TModel> Source<TModel>(
        this IConfiglueSources<TModel> options,
        CommonSourceSelector selector
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selector);
        return options.Source(SourceKey<TModel>.Named(selector.SourceId));
    }
}
