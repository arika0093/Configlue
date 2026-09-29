using Configlue.Extensions.MSOptions;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Tests;

public sealed partial class StateRuntimeTests
{
    [Test]
    public async Task DependencyInjection_ResolvesMergedOptionsAndSavesToConfiguredSource()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(4),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("defaults.local"),
                    }
                ),
                Plugins = Optional<IReadOnlyList<string>>.Present(["base"]),
            }
        );
        var user = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Enabled = Optional<bool>.Present(false),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
                ),
                Plugins = Optional<IReadOnlyList<string>>.Present(["user"]),
            }
        );
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults, priority: 0),
        ]);
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            _ => sources,
            StateWriteRoute.To("user")
        );
        using var serviceProvider = services.BuildServiceProvider();
        var readOnly = serviceProvider.GetRequiredService<IReadOnlyState<AppSettings>>();
        var writable = serviceProvider.GetRequiredService<IWritableState<AppSettings>>();

        (ReferenceEquals(readOnly, writable)).ShouldBeTrue();
        var resolved = await ((IConfiglueRuntimeState<AppSettings>)readOnly).ReadAsync();
        var currentValue = await readOnly.GetValueAsync();
        var saveResult = await writable.SaveAsync(patch =>
        {
            patch.Enabled = true;
            patch.RetryCount = 10;
            patch.Label = "saved";
            patch.Database.Host = "saved.local";
            patch.Database.Port = 7443;
            patch.Plugins = new[] { "saved-plugin" };
        });
        var written = await user.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.SourceId).ShouldBe("user");
        (resolved.Revisions!.Revisions.Count).ShouldBe(2);
        (resolved.Value!.Enabled).ShouldBeFalse();
        (currentValue.RetryCount).ShouldBe(4);
        (resolved.Value.RetryCount).ShouldBe(4);
        (resolved.Value.Database!.Host).ShouldBe("defaults.local");
        (resolved.Value.Database.Port).ShouldBe(6432);
        ((resolved.Value.Plugins))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "base", "user" }).OrderBy(static item => item));
        (saveResult.Revision).ShouldBe("2");
        (written.Value!.RetryCount.Value).ShouldBe(10);
        (written.Value.Database!.Value!.Host.Value).ShouldBe("saved.local");
        ((written.Value.Plugins.Value!))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "saved-plugin" }).OrderBy(static item => item));
    }

    [Test]
    public async Task DependencyInjection_ProvidesMicrosoftOptionsAdapters()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(17) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("default", store, writer: store)])
        );
        using var serviceProvider = services.BuildServiceProvider();

        var options = serviceProvider.GetRequiredService<IOptions<AppSettings>>();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        using var scope = serviceProvider.CreateScope();
        var snapshot = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<AppSettings>>();
        var firstSnapshot = snapshot.Value;
        var secondSnapshot = snapshot.Get(Options.DefaultName);

        (options.Value.RetryCount).ShouldBe(17);
        (monitor.CurrentValue.RetryCount).ShouldBe(17);
        (ReferenceEquals(firstSnapshot, secondSnapshot)).ShouldBeTrue();
        (firstSnapshot.RetryCount).ShouldBe(17);
    }

    [Test]
    public async Task OptionsSnapshot_CachesDefaultAndNamedProfilesWithinScope()
    {
        var defaultStore = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(17) }
        );
        var namedStore = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("default", defaultStore, writer: defaultStore),
            ])
        );
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            "custom",
            new StateSourceSet<AppSettings.Fragment>([
                new("custom", namedStore, writer: namedStore),
            ])
        );
        using var serviceProvider = services.BuildServiceProvider();

        using var firstScope = serviceProvider.CreateScope();
        var snapshot = firstScope.ServiceProvider.GetRequiredService<
            IOptionsSnapshot<AppSettings>
        >();
        (snapshot.Value.RetryCount).ShouldBe(17);
        (snapshot.Get("custom").RetryCount).ShouldBe(8);

        await serviceProvider
            .GetRequiredService<IWritableState<AppSettings>>()
            .SaveAsync(settings => settings.RetryCount = 23);
        await serviceProvider
            .GetRequiredKeyedService<IWritableState<AppSettings>>("custom")
            .SaveAsync(settings => settings.RetryCount = 19);

        (snapshot.Value.RetryCount).ShouldBe(17);
        (snapshot.Get("custom").RetryCount).ShouldBe(8);

        using var secondScope = serviceProvider.CreateScope();
        var updatedSnapshot = secondScope.ServiceProvider.GetRequiredService<
            IOptionsSnapshot<AppSettings>
        >();
        (updatedSnapshot.Value.RetryCount).ShouldBe(23);
        (updatedSnapshot.Get("custom").RetryCount).ShouldBe(19);
    }

    [Test]
    public async Task OptionsMonitor_ResolvesNamedProfilesAndPublishesTheirChanges()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var custom = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("default", defaults, writer: defaults)]),
            onChangeDebounce: TimeSpan.Zero
        );
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            "custom",
            new StateSourceSet<AppSettings.Fragment>([
                new("custom", custom, writer: custom, watcher: custom),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        using var serviceProvider = services.BuildServiceProvider();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        var changed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var directChanged = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = monitor.OnChange(
            (value, name) =>
            {
                if (name == "custom")
                {
                    changed.TrySetResult(value.RetryCount);
                }
            }
        );
        using var directSubscription = serviceProvider
            .GetRequiredKeyedService<IReadOnlyState<AppSettings>>("custom")
            .OnChange(value => directChanged.TrySetResult(value.RetryCount));

        (monitor.Get("custom").RetryCount).ShouldBe(8);
        custom.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) });
        (await directChanged.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(12);
        (await changed.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(12);
    }

    [Test]
    public async Task OptionsMonitor_FollowsProfilesAddedToRuntimeRegistry()
    {
        var stores = new Dictionary<string, InMemoryStateStore<AppSettings.Fragment>>(
            StringComparer.Ordinal
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueStateRegistry<AppSettings, AppSettings.Fragment>(
            (_, profileName) =>
            {
                var store = new InMemoryStateStore<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
                );
                stores.Add(profileName, store);
                return new StateSourceSet<AppSettings.Fragment>([
                    new(profileName, store, writer: store, watcher: store),
                ]);
            },
            onChangeDebounce: TimeSpan.Zero
        );
        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IConfiglueStateRegistry<AppSettings>>();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        var changed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = monitor.OnChange(
            (value, name) =>
            {
                if (name == "runtime")
                {
                    changed.TrySetResult(value.RetryCount);
                }
            }
        );

        (registry.TryAdd("runtime")).ShouldBeTrue();
        (monitor.Get("runtime").RetryCount).ShouldBe(4);
        stores["runtime"].Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(14) });
        (await changed.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(14);
    }

    [Test]
    public async Task OptionsMonitor_RemainsSubscribedWhenProfileIsReAddedDuringRemoval()
    {
        var stores = new Dictionary<string, InMemoryStateStore<AppSettings.Fragment>>(
            StringComparer.Ordinal
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueStateRegistry<AppSettings, AppSettings.Fragment>(
            (_, profileName) =>
            {
                var store = new InMemoryStateStore<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
                );
                stores[profileName] = store;
                return new StateSourceSet<AppSettings.Fragment>([
                    new(profileName, store, writer: store, watcher: store),
                ]);
            },
            onChangeDebounce: TimeSpan.Zero
        );
        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IConfiglueStateRegistry<AppSettings>>();
        (registry.TryAdd("runtime")).ShouldBeTrue();

        var removalEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var continueRemoval = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        // Pause before the monitor's removal handler so the replacement is added first.
        registry.StateRemoved += name =>
        {
            if (name == "runtime")
            {
                removalEntered.TrySetResult();
                continueRemoval.Task.GetAwaiter().GetResult();
            }
        };

        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        var replacementChanged = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = monitor.OnChange(
            (value, name) =>
            {
                if (name == "runtime" && value.RetryCount == 18)
                {
                    replacementChanged.TrySetResult(value.RetryCount);
                }
            }
        );

        var removal = Task.Run(() => registry.TryRemove("runtime"));
        Task<bool>? replacementAdd = null;
        try
        {
            await removalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            replacementAdd = Task.Run(() => registry.TryAdd("runtime"));
            (
                await Task.Run(() =>
                    SpinWait.SpinUntil(
                        () => registry.TryGet("runtime", out _),
                        TimeSpan.FromSeconds(5)
                    )
                )
            ).ShouldBeTrue();
        }
        finally
        {
            continueRemoval.TrySetResult();
        }

        (await removal).ShouldBeTrue();
        (await replacementAdd!).ShouldBeTrue();
        stores["runtime"].Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(18) });

        (await replacementChanged.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(18);
    }
}
