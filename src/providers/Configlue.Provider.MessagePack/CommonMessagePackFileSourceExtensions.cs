using Configlue.Provider.MessagePack;
using MessagePack;

namespace Configlue.Source.Presets;

/// <summary>Adds MessagePack file registration to the common source preset.</summary>
public static class CommonMessagePackFileSourceExtensions
{
    /// <summary>Selects the MessagePack provider for this common file source.</summary>
    public static CommonFileSourceBuilder MessagePack(
        this CommonFileSourceBuilder source,
        MessagePackSerializerOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        source.SetProviderRegistration(
            new MessagePackProviderOptions { Options = options },
            static (sources, settings, providerOptions) =>
                sources.FromMessagePackFile(
                    new MessagePackFileSourceOptions
                    {
                        Id = settings.Id,
                        Path = settings.Path,
                        Priority = settings.Priority,
                        ReadOnly = settings.ReadOnly,
                        ExplicitOnly = settings.ExplicitOnly,
                        WatchChanges = settings.WatchChanges,
                        SerializerOptions = providerOptions.Options,
                        ResourceOptions = settings.ResourceOptions,
                        Transformers = settings.Transformers.ToArray(),
                        SectionPath = settings.SectionPath,
                    }
                )
        );
        return source;
    }

    /// <summary>Sets MessagePack serialization options for this common file source.</summary>
    public static CommonFileSourceBuilder SerializerOptions(
        this CommonFileSourceBuilder source,
        MessagePackSerializerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        return source.MessagePack(options);
    }

    private sealed class MessagePackProviderOptions
    {
        public MessagePackSerializerOptions? Options { get; set; }
    }
}
