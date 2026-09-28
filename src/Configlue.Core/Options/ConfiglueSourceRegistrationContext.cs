namespace Configlue;

/// <summary>Runtime source registration inputs for a named generated model.</summary>
/// <typeparam name="TModel">The generated model.</typeparam>
public sealed class ConfiglueSourceRegistrationContext<TModel>
    where TModel : IConfiglueFacadeModel<TModel>
{
    internal ConfiglueSourceRegistrationContext(
        string optionsName,
        IServiceProvider? services,
        ConfiglueSourceSetBuilder<TModel> sources
    )
    {
        OptionsName = optionsName;
        Services = services;
        Sources = sources;
    }

    /// <summary>The named options instance being created.</summary>
    public string OptionsName { get; }

    /// <summary>Application services, or null for an independent context.</summary>
    public IServiceProvider? Services { get; }

    /// <summary>The source registrations for this instance.</summary>
    public ConfiglueSourceSetBuilder<TModel> Sources { get; }
}
