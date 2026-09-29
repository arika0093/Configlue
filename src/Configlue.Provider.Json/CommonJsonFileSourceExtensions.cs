using System.Text.Json;
using System.Text.Json.Serialization;
using Configlue.Source.Presets;

namespace Configlue.Provider.Json;

/// <summary>Adds JSON file-provider options to common source presets.</summary>
public static class CommonJsonFileSourceExtensions
{
    /// <summary>Selects the JSON provider for this common file source.</summary>
    public static CommonFileSourceBuilder Json(
        this CommonFileSourceBuilder source,
        JsonSerializerOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        var providerOptions = source.GetOrCreateProviderOptions(static () =>
            new JsonProviderOptions()
        );
        providerOptions.SerializerOptions = options;
        Apply(source, providerOptions);
        return source;
    }

    /// <summary>Selects JSON using generated metadata from a serializer context.</summary>
    public static CommonFileSourceBuilder Json(
        this CommonFileSourceBuilder source,
        JsonSerializerContext context
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        return source.Json(context.Options);
    }

    /// <summary>Sets JSON serialization options for this common file source.</summary>
    public static CommonFileSourceBuilder SerializerOptions(
        this CommonFileSourceBuilder source,
        JsonSerializerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        var providerOptions = source.GetOrCreateProviderOptions(static () =>
            new JsonProviderOptions()
        );
        providerOptions.SerializerOptions = options;
        Apply(source, providerOptions);
        return source;
    }

    private static void Apply(CommonFileSourceBuilder source, JsonProviderOptions options) =>
        source.SetProviderRegistration(
            options,
            static (sources, settings, jsonOptions) =>
                sources.FromJsonFile(
                    new JsonFileSourceOptions
                    {
                        Id = settings.Id,
                        Path = settings.Path,
                        Priority = settings.Priority,
                        SectionPath = settings.SectionPath,
                        SchemaReferenceBaseUri = settings.SchemaReferenceBaseUri,
                        ReadOnly = settings.ReadOnly,
                        ExplicitOnly = settings.ExplicitOnly,
                        WatchChanges = settings.WatchChanges,
                        SerializerOptions = jsonOptions.SerializerOptions,
                        ResourceOptions = settings.ResourceOptions,
                        Transformers = settings.Transformers,
                    }
                )
        );

    private sealed class JsonProviderOptions
    {
        public JsonSerializerOptions? SerializerOptions { get; set; }
    }
}
