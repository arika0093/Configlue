namespace Configlue;

/// <summary>Provides concise process-wide and lifetime-managed Configlue entry points.</summary>
public static class ConfiglueApp
{
    /// <summary>Initializes the process-wide default context.</summary>
    public static void Initialize(Action<ConfiglueBuilder> configure) =>
        Configlue.Initialize(configure);

    /// <summary>Creates an independent, lifetime-managed context.</summary>
    public static ConfiglueContext CreateContext(Action<ConfiglueBuilder> configure) =>
        Configlue.CreateContext(configure);

    /// <summary>Gets options from the initialized process-wide context.</summary>
    public static IWritableOptions<TModel> GetOptions<TModel>(string? optionsName = null) =>
        Configlue.GetOptions<TModel>(optionsName);

    /// <summary>Disposes the initialized process-wide context and clears it for later initialization.</summary>
    public static ValueTask ShutdownAsync() => Configlue.ShutdownAsync();
}
