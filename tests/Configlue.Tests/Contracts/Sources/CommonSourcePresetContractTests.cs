using Configlue;
using Configlue.Source.Presets;

namespace Configlue.Tests;

public sealed class CommonSourcePresetContractTests
{
    [Test]
    public void DefaultWriteLayer_RequiresAConfiguredFile()
    {
        Should.Throw<InvalidOperationException>(() =>
            ConfiglueApp.CreateContext(builder =>
                builder.UseCommonSources(sources =>
                {
                    sources.DefaultWriteLayer(CommonSourceLayer.UserGlobal);
                    sources.WithEnvironment("CONFIGLUE_TEST");
                    sources.Add<AppSettings>();
                })
            )
        );
    }

    [Test]
    public void CommonSources_RequireAtLeastOneEnabledSource()
    {
        Should.Throw<InvalidOperationException>(() =>
            ConfiglueApp.CreateContext(builder => builder.UseCommonSources(_ => { }))
        );
    }
}
