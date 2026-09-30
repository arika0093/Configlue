using Configlue.CompilerServices;

namespace Configlue;

/// <summary>Runtime source registration inputs for a named generated model.</summary>
/// <typeparam name="TModel">The generated model.</typeparam>
public sealed class ConfiglueSourceRegistrationContext<TModel>
    where TModel : IConfiglueFacadeModel<TModel>
{
    internal ConfiglueSourceRegistrationContext(
        string stateName,
        IServiceProvider? services,
        ConfiglueSourceSetBuilder<TModel> sources
    )
    {
        StateName = stateName;
        Services = services;
        Sources = sources;
    }

    /// <summary>The named state instance being created.</summary>
    public string StateName { get; }

    /// <summary>Application services, or null for an independent context.</summary>
    public IServiceProvider? Services { get; }

    /// <summary>The source registrations for this instance.</summary>
    public ConfiglueSourceSetBuilder<TModel> Sources { get; }
}
