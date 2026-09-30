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

    public static async Task<bool> CanResolveAsync(
        IConfiglueInspection<PortableSettings> inspection,
        CancellationToken cancellationToken = default
    )
    {
        var check = inspection.Check(cancellationToken);
        await foreach (var source in check)
        {
            _ = source.Status;
        }

        var result = await check.Result;
        return result.IsResolved;
    }
}
