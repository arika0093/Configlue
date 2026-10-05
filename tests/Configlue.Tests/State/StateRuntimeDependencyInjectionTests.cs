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
}
