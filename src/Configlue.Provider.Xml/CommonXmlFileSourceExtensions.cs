using Configlue;
using Configlue.Provider.Xml;

namespace Configlue.Source.Presets;

/// <summary>Adds XML file registration to the common source preset.</summary>
public static class CommonXmlFileSourceExtensions
{
    /// <summary>Selects the XML provider for this common file source.</summary>
    public static CommonFileSourceBuilder Xml(this CommonFileSourceBuilder source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.SetProviderRegistration(
            static (sources, settings) =>
                sources.FromXmlFile(
                    new XmlFileSourceOptions
                    {
                        Id = settings.Id,
                        Path = settings.Path,
                        Priority = settings.Priority,
                        SectionPath = settings.SectionPath,
                        ReadOnly = settings.ReadOnly,
                        ExplicitOnly = settings.ExplicitOnly,
                        WatchChanges = settings.WatchChanges,
                        ResourceOptions = settings.ResourceOptions,
                        Transformers = settings.Transformers.ToArray(),
                    }
                )
        );
        return source;
    }
}
