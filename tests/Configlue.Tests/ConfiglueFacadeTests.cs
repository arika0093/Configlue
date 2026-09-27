using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Tests;

public sealed class ConfiglueFacadeTests
{
    [Test]
    public async Task ContextAndStaticFacadeUseGeneratedOneArityRegistration()
    {
        var source = CreateSource("context", "context-value");
        await using (
            var context = Configlue.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model => model.Sources(sources => sources.Add(source)));
            })
        )
        {
            var value = await context.GetOptions<AppSettings>().GetValueAsync();
            (value.Label).ShouldBe("context-value");
        }

        Configlue.Initialize(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(CreateSource("static", "static-value")))
            );
        });
        try
        {
            var value = await Configlue.GetOptions<AppSettings>().GetValueAsync();
            (value.Label).ShouldBe("static-value");

            Should.Throw<InvalidOperationException>(() => Configlue.Initialize(_ => { }));
        }
        finally
        {
            await Configlue.ShutdownAsync();
        }
    }

    [Test]
    public async Task DiFacadeUsesSameRegistrationForConfiglueAndMicrosoftOptions()
    {
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(_ => CreateSource("di", "di-value")))
            );
        });

        await using var provider = services.BuildServiceProvider();
        var readOnly = provider.GetRequiredService<IReadOnlyOptions<AppSettings>>();
        var writable = provider.GetRequiredService<IWritableOptions<AppSettings>>();
        var value = await readOnly.GetValueAsync();

        (ReferenceEquals(readOnly, writable)).ShouldBeTrue();
        (value.Label).ShouldBe("di-value");
        (provider.GetRequiredService<IOptions<AppSettings>>().Value.Label).ShouldBe("di-value");
    }

    [Test]
    public async Task DiFacadeRegistersNamedInstancesAsKeyedAndMicrosoftOptions()
    {
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.OptionsName = "profile";
                model.Sources(sources =>
                    sources.Add(_ => CreateSource("profile", "profile-value"))
                );
            });
        });

        await using var provider = services.BuildServiceProvider();
        var keyedOptions = provider.GetRequiredKeyedService<IReadOnlyOptions<AppSettings>>(
            "profile"
        );

        (await keyedOptions.GetValueAsync()).Label.ShouldBe("profile-value");
        (provider.GetRequiredService<IOptionsMonitor<AppSettings>>().Get("profile").Label).ShouldBe(
            "profile-value"
        );
    }

    [Test]
    public async Task ContextResolvesNamedModelInstances()
    {
        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.OptionsName = "first";
                model.Sources(sources => sources.Add(CreateSource("first", "first-value")));
            });
            builder.Add<AppSettings>(model =>
            {
                model.OptionsName = "second";
                model.Sources(sources => sources.Add(CreateSource("second", "second-value")));
            });
        });

        (await context.GetOptions<AppSettings>("first").GetValueAsync()).Label.ShouldBe(
            "first-value"
        );
        (await context.GetOptions<AppSettings>("second").GetValueAsync()).Label.ShouldBe(
            "second-value"
        );
        Should.Throw<KeyNotFoundException>(() => context.GetOptions<AppSettings>());
    }

    [Test]
    public async Task ProfileCatalogReconciliationKeepsUnrelatedDynamicOptions()
    {
        var catalogStore = new InMemoryStateStore<ConfiglueProfileCatalog>(
            new ConfiglueProfileCatalog
            {
                ProfileNames = ["default"],
                ActiveProfileName = "default",
            }
        );
        var catalog = new StateSource<ConfiglueProfileCatalog>(
            "catalog",
            catalogStore,
            writer: catalogStore,
            watcher: catalogStore
        );
        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.SourcesForOptions((name, sources) =>
                    sources.Add(_ =>
                        CreateSource(
                            string.IsNullOrEmpty(name) ? "default-source" : $"profile-{name}",
                            $"{name}-value"
                        )
                    )
                );
            });
        });

        var registry = context.GetOptionsRegistry<AppSettings>();
        registry.TryAdd("temporary").ShouldBeTrue();
        var profiles = context.GetProfiledOptions<AppSettings>();

        (await profiles.GetProfileNamesAsync()).ShouldContain("default");
        registry.TryGet("temporary", out var temporary).ShouldBeTrue();
        (await temporary!.GetValueAsync()).Label.ShouldBe("temporary-value");
    }

    [Test]
    public async Task FacadeProfilesCreateSwitchAndRemoveNamedOptionsInContext()
    {
        var catalogStore = new InMemoryStateStore<ConfiglueProfileCatalog>();
        var catalog = new StateSource<ConfiglueProfileCatalog>(
            "catalog",
            catalogStore,
            writer: catalogStore,
            watcher: catalogStore
        );
        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.SourcesForOptions((name, sources) =>
                    sources.Add(_ =>
                        CreateSource(
                            string.IsNullOrEmpty(name) ? "fixed" : $"profile-{name}",
                            string.IsNullOrEmpty(name) ? "fixed-value" : $"{name}-value"
                        )
                    )
                );
            });
        });

        var profiles = context.GetProfiledOptions<AppSettings>();
        await profiles.GetProfileNamesAsync();
        await profiles.CreateProfileAsync("Work", copyFrom: "default");
        (await context.GetOptions<AppSettings>("Work").GetValueAsync()).Label.ShouldBe("default-value");
        await profiles.SetActiveProfileAsync("Work");
        (await profiles.GetActiveValueAsync()).Label.ShouldBe("default-value");

        var removedHandle = context.GetOptions<AppSettings>("Work");
        await profiles.RemoveProfileAsync("Work");
        await Should.ThrowAsync<ObjectDisposedException>(async () => await removedHandle.GetValueAsync());
        Should.Throw<KeyNotFoundException>(() => context.GetOptions<AppSettings>("Work"));
        (await context.GetOptions<AppSettings>("default").GetValueAsync()).Label.ShouldBe("default-value");
    }

    [Test]
    public async Task FacadeDynamicOptionsUpdateDiMonitorAndInvalidateRemovedHandles()
    {
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableDynamicOptions = true;
                model.SourcesForOptions((name, sources) =>
                    sources.Add(_ => CreateSource($"dynamic-{name}", $"{name}-value"))
                );
            });
        });

        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IConfiglueOptionsRegistry<AppSettings>>();
        registry.TryAdd("late").ShouldBeTrue();
        var monitor = provider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        (monitor.Get("late").Label).ShouldBe("late-value");
        var handle = registry.Get("late");
        await handle.SaveAsync(settings => settings.Label = "updated");
        (monitor.Get("late").Label).ShouldBe("updated");

        (await registry.TryRemoveAsync("late")).ShouldBeTrue();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await handle.GetValueAsync());
        Should.Throw<KeyNotFoundException>(() => monitor.Get("late"));
    }

    [Test]
    public async Task FacadeProfilesAreVisibleThroughTheDiMonitorUntilRemoval()
    {
        var catalogStore = new InMemoryStateStore<ConfiglueProfileCatalog>();
        var catalog = new StateSource<ConfiglueProfileCatalog>(
            "catalog",
            catalogStore,
            writer: catalogStore,
            watcher: catalogStore
        );
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.SourcesForOptions((name, sources) =>
                    sources.Add(_ =>
                        CreateSource(
                            $"di-profile-{name}",
                            $"{name}-value"
                        )
                    )
                );
            });
        });

        await using var provider = services.BuildServiceProvider();
        var profiles = provider.GetRequiredService<IConfiglueProfiledOptions<AppSettings>>();
        await profiles.GetProfileNamesAsync();
        await profiles.CreateProfileAsync("Work", copyFrom: "default");
        var monitor = provider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        (monitor.Get("Work").Label).ShouldBe("default-value");
        await profiles.SetActiveProfileAsync("Work");
        (await profiles.GetActiveValueAsync()).Label.ShouldBe("default-value");

        await profiles.RemoveProfileAsync("Work");
        Should.Throw<KeyNotFoundException>(() => monitor.Get("Work"));
        (await profiles.GetActiveProfileNameAsync()).ShouldBe("default");
    }

    private static StateSource<AppSettings.Fragment> CreateSource(string id, string label)
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present(label) }
        );
        return new StateSource<AppSettings.Fragment>(id, store, writer: store, watcher: store);
    }
}
