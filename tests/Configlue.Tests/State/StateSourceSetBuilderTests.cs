using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed class StateSourceSetBuilderTests
{
    [Test]
    public async Task Builder_DetectsAndOverridesSourceCapabilities()
    {
        var autoDetected = new InMemoryStateSource<AppSettings.Fragment>();
        var reader = new ReaderOnly<AppSettings.Fragment>(new AppSettings.Fragment());
        var writerOverride = new InMemoryStateSource<AppSettings.Fragment>();
        var watcherOverride = new InMemoryStateSource<AppSettings.Fragment>();
        var suppressedCapabilities = new InMemoryStateSource<AppSettings.Fragment>();
        var sources = new StateSourceSetBuilder<AppSettings.Fragment>();

        sources.AddTestStore("automatic", autoDetected, priority: 50);
        sources
            .Add(
                "custom",
                reader,
                priority: 10,
                fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable
                    | StateFallbackCondition.InvalidPayload,
                physicalOrigin: "settings.json",
                fixedResourceId: new ResourceId("file:settings.json")
            )
            .WithWriter(writerOverride)
            .WithWatcher(watcherOverride);
        sources.Add("read-only", reader, priority: 0);
        sources
            .Add("suppressed", suppressedCapabilities, priority: -1)
            .WithoutWriter()
            .WithoutWatcher();

        var sourceSet = sources.Build();
        var automatic = sourceSet.Sources[0];
        var custom = sourceSet.Sources[1];
        var readOnly = sourceSet.Sources[2];
        var suppressed = sourceSet.Sources[3];

        (ReferenceEquals(automatic.Writer, autoDetected)).ShouldBeTrue();
        (ReferenceEquals(automatic.Watcher, autoDetected)).ShouldBeTrue();
        (ReferenceEquals(custom.Writer, writerOverride)).ShouldBeTrue();
        (ReferenceEquals(custom.Watcher, watcherOverride)).ShouldBeTrue();
        (custom.FallbackCondition).ShouldBe(
            StateFallbackCondition.NotFoundOrUnavailable | StateFallbackCondition.InvalidPayload
        );
        (custom.PhysicalOrigin).ShouldBe("settings.json");
        (custom.FixedResourceId).ShouldBe(new ResourceId("file:settings.json"));
        (readOnly.Writer).ShouldBeNull();
        (readOnly.Watcher).ShouldBeNull();
        (suppressed.Writer).ShouldBeNull();
        (suppressed.Watcher).ShouldBeNull();
    }

    [Test]
    public async Task DependencyInjectionBuilder_ResolvesProvidersAndUsesDetectedCapabilities()
    {
        var user = new InMemoryStateSource<AppSettings.Fragment>();
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
        );
        var configureCount = 0;
        var services = new ServiceCollection();
        services.AddKeyedSingleton("user", user);
        services.AddKeyedSingleton("defaults", defaults);
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            (provider, sources) =>
            {
                configureCount++;
                sources.Add(
                    "user",
                    provider.GetRequiredKeyedService<InMemoryStateSource<AppSettings.Fragment>>(
                        "user"
                    ),
                    priority: 100,
                    fallbackCondition: StateFallbackCondition.NotFound
                );
                sources.Add(
                    "defaults",
                    provider.GetRequiredKeyedService<InMemoryStateSource<AppSettings.Fragment>>(
                        "defaults"
                    ),
                    priority: 0
                );
            },
            StateWritePlan.DefaultTo(SourceId.From("user")),
            onChangeDebounce: TimeSpan.Zero
        );
        using var serviceProvider = services.BuildServiceProvider();

        var options = serviceProvider.GetRequiredService<
            ConfiglueRuntime<AppSettings, AppSettings.Fragment>
        >();
        var initial = await options.ReadAsync();
        var changed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = options.OnChange(value => changed.TrySetResult(value.RetryCount));
        await options.SaveAsync(patch => patch.RetryCount = 9);
        var savedUserState = await user.ReadAsync();
        var changedRetryCount = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (initial.SourceId).ShouldBe(SourceId.From("defaults"));
        (initial.Value!.RetryCount).ShouldBe(4);
        (savedUserState.Value!.RetryCount.Value).ShouldBe(9);
        (changedRetryCount).ShouldBe(9);
        (configureCount).ShouldBe(1);
    }

    [Test]
    public async Task DependencyInjectionBuilder_RegistersKeyedProfiles()
    {
        var profile = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) }
        );
        var services = new ServiceCollection();
        services.AddKeyedSingleton("profile-source", profile);
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            "profile",
            (provider, sources) =>
                sources.Add(
                    "profile-source",
                    provider.GetRequiredKeyedService<InMemoryStateSource<AppSettings.Fragment>>(
                        "profile-source"
                    )
                )
        );
        using var serviceProvider = services.BuildServiceProvider();

        var options = serviceProvider.GetRequiredKeyedService<IReadOnlyState<AppSettings>>(
            "profile"
        );
        var value = await options.GetValueAsync();

        (value.RetryCount).ShouldBe(12);
    }

    private sealed class ReaderOnly<T>(T value) : ISourceReader<T>
    {
        public ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            return ValueTaskCompat.FromResult(StateReadResult<T>.Success(value, "reader-only"));
        }
    }
}

internal static class StateSourceSetBuilderTestExtensions
{
    public static StateSourceBuilder<T> AddTestStore<T>(
        this StateSourceSetBuilder<T> sources,
        string id,
        InMemoryStateSource<T> store,
        int priority = 0
    ) => sources.Add(id, store, priority);
}
