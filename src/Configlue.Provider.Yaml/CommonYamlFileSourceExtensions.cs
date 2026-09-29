using System.Text.Json;
using Configlue;
using Configlue.Provider.Yaml;
using SharpYaml;

namespace Configlue.Source.Presets;

/// <summary>Adds YAML file registration to the common source preset.</summary>
public static class CommonYamlFileSourceExtensions
{
    /// <summary>Selects the YAML provider for this common file source.</summary>
    public static CommonFileSourceBuilder Yaml(
        this CommonFileSourceBuilder source,
        YamlSerializerOptions? serializerOptions = null,
        JsonNamingPolicy? propertyNamingPolicy = null
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        var options = source.GetOrCreateProviderOptions(static () => new YamlProviderOptions());
        options.SerializerOptions = serializerOptions;
        options.PropertyNamingPolicy = propertyNamingPolicy;
        source.SetProviderRegistration(
            options,
            static (sources, settings, yamlOptions) =>
                sources.FromYamlFile(
                    new YamlFileSourceOptions
                    {
                        Id = settings.Id,
                        Path = settings.Path,
                        Priority = settings.Priority,
                        SectionPath = settings.SectionPath,
                        SchemaReferenceBaseUri = settings.SchemaReferenceBaseUri,
                        ReadOnly = settings.ReadOnly,
                        ExplicitOnly = settings.ExplicitOnly,
                        WatchChanges = settings.WatchChanges,
                        PropertyNamingPolicy = yamlOptions.PropertyNamingPolicy,
                        SerializerOptions = yamlOptions.SerializerOptions,
                        ResourceOptions = settings.ResourceOptions,
                        Transformers = settings.Transformers.ToArray(),
                    }
                )
        );
        return source;
    }

    /// <summary>Sets YAML serializer options for this common file source.</summary>
    public static CommonFileSourceBuilder YamlSerializerOptions(
        this CommonFileSourceBuilder source,
        YamlSerializerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        source
            .GetOrCreateProviderOptions(static () => new YamlProviderOptions())
            .SerializerOptions = options;
        return source;
    }

    private sealed class YamlProviderOptions
    {
        public YamlSerializerOptions? SerializerOptions { get; set; }
        public JsonNamingPolicy? PropertyNamingPolicy { get; set; }
    }
}
