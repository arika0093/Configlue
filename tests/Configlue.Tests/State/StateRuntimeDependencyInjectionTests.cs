using Configlue.Extensions.MSOptions;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Tests;

public sealed partial class StateRuntimeTests
{
    [Test]
    public async Task DependencyInjection_ResolvesMergedOptionsAndSavesToConfiguredSource()
    {
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
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
        var user = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Enabled = Optional<bool>.Present(false),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
                ),
                Plugins = Optional<IReadOnlyList<string>>.Present(["user"]),
            }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("user"));
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "user",
                            user,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Priority = 100,
                                Writer = user,
                            }
                        )
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "defaults",
                            defaults,
                            new StateSourceOptions<AppSettings.Fragment> { Priority = 0 }
                        )
                    );
                });
            });
        });
        await using var serviceProvider = services.BuildServiceProvider();
        var readOnly = serviceProvider.GetRequiredService<IReadOnlyState<AppSettings>>();
        var writable = serviceProvider.GetRequiredService<IWritableState<AppSettings>>();

        (ReferenceEquals(readOnly, writable)).ShouldBeTrue();
        var diagnostics = ((IConfiglueDiagnostics<AppSettings>)readOnly).GetDiagnostics();
        (diagnostics.Sources.Count).ShouldBe(2);
        (
            diagnostics.Sources.Any(static source => source.Id == SourceId.From("user"))
        ).ShouldBeTrue();
        (
            diagnostics.Sources.Any(static source => source.Id == SourceId.From("defaults"))
        ).ShouldBeTrue();
        (diagnostics.DefaultWriteSourceId).ShouldBe(SourceId.From("user"));
        var check = ((IConfiglueDiagnostics<AppSettings>)readOnly).Check();
        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }
        var checkResult = await check.Result;
        (checkResult.IsResolved).ShouldBeTrue();
        (streamed.Count(static source => source.Contributed)).ShouldBe(2);
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

        (currentValue.RetryCount).ShouldBe(4);
        (currentValue.Enabled).ShouldBeFalse();
        (currentValue.Database!.Host).ShouldBe("defaults.local");
        (currentValue.Database.Port).ShouldBe(6432);
        ((currentValue.Plugins))
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
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(17) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "default",
                            store,
                            new StateSourceOptions<AppSettings.Fragment> { Writer = store }
                        )
                    )
                )
            );
        });
        await using var serviceProvider = services.BuildServiceProvider();

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
        var defaultStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(17) }
        );
        var namedStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "default",
                            defaultStore,
                            new StateSourceOptions<AppSettings.Fragment> { Writer = defaultStore }
                        )
                    )
                )
            );
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "custom";
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "custom",
                            namedStore,
                            new StateSourceOptions<AppSettings.Fragment> { Writer = namedStore }
                        )
                    )
                );
            });
        });
        await using var serviceProvider = services.BuildServiceProvider();

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
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var custom = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.OnChangeDebounce = TimeSpan.Zero;
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "default",
                            defaults,
                            new StateSourceOptions<AppSettings.Fragment> { Writer = defaults }
                        )
                    )
                );
            });
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "custom";
                model.OnChangeDebounce = TimeSpan.Zero;
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "custom",
                            custom,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Writer = custom,
                                Watcher = custom,
                            }
                        )
                    )
                );
            });
        });
        await using var serviceProvider = services.BuildServiceProvider();
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
}
