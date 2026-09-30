using Configlue.Testing;

namespace Configlue.Tests;

public sealed class ConfiglueAppLifecycleTests
{
    [Test]
    [NotInParallel]
    public async Task UninitializedAccessThrowsAndShutdownIsIdempotent()
    {
        await ConfiglueApp.ShutdownAsync();

        Should.Throw<InvalidOperationException>(() => ConfiglueApp.GetState<AppSettings>());

        await ConfiglueApp.ShutdownAsync();
    }

    [Test]
    [NotInParallel]
    public async Task ShutdownClearsTheDefaultContextAndAllowsReinitialization()
    {
        await ConfiglueApp.ShutdownAsync();
        try
        {
            ConfiglueApp.Initialize(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources => sources.Add(CreateSource("first", "first")))
                );
            });
            (await ConfiglueApp.GetState<AppSettings>().GetValueAsync()).Label.ShouldBe("first");
        }
        finally
        {
            await ConfiglueApp.ShutdownAsync();
        }

        Should.Throw<InvalidOperationException>(() => ConfiglueApp.GetState<AppSettings>());

        try
        {
            ConfiglueApp.Initialize(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources => sources.Add(CreateSource("second", "second")))
                );
            });
            (await ConfiglueApp.GetState<AppSettings>().GetValueAsync()).Label.ShouldBe("second");
        }
        finally
        {
            await ConfiglueApp.ShutdownAsync();
        }
    }

    private static StateSource<AppSettings.Fragment> CreateSource(string id, string label)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present(label) }
        );
        return new StateSource<AppSettings.Fragment>(id, store, writer: store, watcher: store);
    }
}
