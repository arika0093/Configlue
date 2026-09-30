using System.Text.Json.Serialization;
using Configlue;
using Configlue.CompilerServices;
using Configlue.Provider.Json;

namespace Configlue.PortableConsumer;

[ConfiglueModel("portable-consumer", Version = 1)]
public partial class PortableSettings
{
    public int RetryCount { get; set; }

    public string? Label { get; set; }
}

public static class PortableConsumerOperations
{
    public static PortableSettings.Fragment ToFragment(PortableSettings model) =>
        ConfiglueModelOperations<PortableSettings, PortableSettings.Fragment>.Current.ToFragment(
            model
        );

    public static JsonConverter<PortableSettings.Fragment> JsonConverter =>
        ConfiglueJsonFragmentRegistry<PortableSettings.Fragment>.Converter;
}
