namespace Configlue;

/// <summary>Provides concise process-wide and lifetime-managed Configlue entry points.</summary>
public static class ConfiglueApp
{
    private static readonly object Gate = new();
    private static ConfiglueContext? _defaultContext;

    /// <summary>Initializes the process-wide default context.</summary>
    public static void Initialize(Action<ConfiglueBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ConfiglueBuilder();
        configure(builder);
        builder.Seal();
        var context = builder.CreateContext();

        lock (Gate)
        {
            if (_defaultContext is not null)
            {
                context.Dispose();
                throw new InvalidOperationException(
                    "The process-wide Configlue context has already been initialized."
                );
            }

            _defaultContext = context;
        }
    }

    /// <summary>Creates an independent, lifetime-managed context.</summary>
    public static ConfiglueContext CreateContext(Action<ConfiglueBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ConfiglueBuilder();
        configure(builder);
        builder.Seal();
        return builder.CreateContext();
    }

    /// <summary>Gets options from the initialized process-wide context.</summary>
    public static IWritableOptions<TModel> GetOptions<TModel>(string? optionsName = null) =>
        GetDefaultContext().GetOptions<TModel>(optionsName);

    /// <summary>Reads resolved state and generated provenance details.</summary>
    public static IConfiglueInspection<TModel> GetInspection<TModel>(string? optionsName = null) =>
        (IConfiglueInspection<TModel>)GetDefaultContext().GetOptions<TModel>(optionsName);

    /// <summary>Opens long-lived drafts of resolved configuration.</summary>
    public static IConfiglueEditSessions<TModel> GetEditSessions<TModel>(
        string? optionsName = null
    ) => (IConfiglueEditSessions<TModel>)GetDefaultContext().GetOptions<TModel>(optionsName);

    /// <summary>Reports source topology and background reload failures.</summary>
    public static IConfiglueDiagnostics<TModel> GetDiagnostics<TModel>(
        string? optionsName = null
    ) => (IConfiglueDiagnostics<TModel>)GetDefaultContext().GetOptions<TModel>(optionsName);

    /// <summary>Administers source-local writes and source migrations.</summary>
    public static IConfiglueSources<TModel> GetSources<TModel>(string? optionsName = null) =>
        (IConfiglueSources<TModel>)GetDefaultContext().GetOptions<TModel>(optionsName);

    /// <summary>Disposes the initialized process-wide context and clears it for later initialization.</summary>
    public static async ValueTask ShutdownAsync()
    {
        ConfiglueContext? context;
        lock (Gate)
        {
            context = _defaultContext;
            _defaultContext = null;
        }

        if (context is not null)
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ConfiglueContext GetDefaultContext()
    {
        lock (Gate)
        {
            return _defaultContext
                ?? throw new InvalidOperationException(
                    "The process-wide Configlue context has not been initialized."
                );
        }
    }
}
