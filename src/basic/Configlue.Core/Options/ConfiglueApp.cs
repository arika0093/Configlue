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

    /// <summary>Gets a state instance from the initialized process-wide context.</summary>
    /// <remarks>
    /// A state instance is addressed by <c>(TModel, StateName)</c>. The default (unnamed) instance
    /// uses <c>stateName: null</c> and is all single-settings applications need.
    /// </remarks>
    public static IWritableState<TModel> GetState<TModel>(string? stateName = null) =>
        GetDefaultContext().GetState<TModel>(stateName);

    /// <summary>Gets the subject-scoping entry point for one state instance.</summary>
    /// <remarks>Subjects scope operations inside the addressed state instance; they never select another instance.</remarks>
    public static ISubjectState<TModel> GetSubjectState<TModel>(string? stateName = null) =>
        GetDefaultContext().GetSubjectState<TModel>(stateName);

    /// <summary>Gets the runtime registry for dynamic named state instances.</summary>
    public static IConfiglueStateRegistry<TModel> GetStateRegistry<TModel>() =>
        GetDefaultContext().GetStateRegistry<TModel>();

    /// <summary>Gets the persisted profile manager for a configured model.</summary>
    /// <remarks>A profile is a catalog-managed named state instance plus active selection.</remarks>
    public static IConfiglueProfiledState<TModel> GetProfiledState<TModel>() =>
        GetDefaultContext().GetProfiledState<TModel>();

    /// <summary>Reads resolved state and generated provenance details.</summary>
    public static IConfiglueInspection<TModel> GetInspection<TModel>(string? stateName = null) =>
        (IConfiglueInspection<TModel>)GetDefaultContext().GetState<TModel>(stateName);

    /// <summary>Opens long-lived drafts of resolved configuration.</summary>
    public static IConfiglueEditSessions<TModel> GetEditSessions<TModel>(
        string? stateName = null
    ) => (IConfiglueEditSessions<TModel>)GetDefaultContext().GetState<TModel>(stateName);

    /// <summary>Reports source topology and background reload failures.</summary>
    public static IConfiglueDiagnostics<TModel> GetDiagnostics<TModel>(string? stateName = null) =>
        (IConfiglueDiagnostics<TModel>)GetDefaultContext().GetState<TModel>(stateName);

    /// <summary>Administers source-local writes and source migrations.</summary>
    public static IConfiglueSources<TModel> GetSources<TModel>(string? stateName = null) =>
        (IConfiglueSources<TModel>)GetDefaultContext().GetState<TModel>(stateName);

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
