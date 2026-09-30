using Configlue;
using Configlue.CompilerServices;

namespace Configlue.PortableCoreConsumer;

[ConfiglueModel("portable-core-consumer", Version = 1)]
public partial class PortableSettings
{
    public int RetryCount { get; set; }
}

public static class PortableConsumerOperations
{
    public static PortableSettings.Fragment ToFragment(PortableSettings model) =>
        ConfiglueModelOperations<PortableSettings, PortableSettings.Fragment>.Current.ToFragment(
            model
        );
}
