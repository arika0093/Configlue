using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed class StateSourceSetBuilderTests
{
    [Test]
    public void BuilderDetectsWriterAndWatcherExposedAsSeparateCapabilityFacets()
    {
        var writer = new InMemoryStateSource<AppSettings.Fragment>();
        var watcher = new InMemoryStateSource<AppSettings.Fragment>();
        var reader = new ReaderWithCapabilities(writer, watcher);
        var builder = new StateSourceSetBuilder<AppSettings.Fragment>();
        builder.Add("faceted", reader);
        var source = builder.Build().Sources.Single();

        source.Writer.ShouldBeSameAs(writer);
        source.Watcher.ShouldBeSameAs(watcher);
    }

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
                fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable,
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
            StateFallbackCondition.NotFoundOrUnavailable
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
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("user"));
                model.OnChangeDebounce = TimeSpan.Zero;
                model.ConfigureSources(registration =>
                {
                    configureCount++;
                    var resolvedUser = registration.Services!.GetRequiredKeyedService<
                        InMemoryStateSource<AppSettings.Fragment>
                    >("user");
                    var resolvedDefaults = registration.Services!.GetRequiredKeyedService<
                        InMemoryStateSource<AppSettings.Fragment>
                    >("defaults");
                    registration.Sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "user",
                            resolvedUser,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Priority = 100,
                                FallbackCondition = StateFallbackCondition.NotFound,
                                Writer = resolvedUser,
                                Watcher = resolvedUser,
                            }
                        )
                    );
                    registration.Sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "defaults",
                            resolvedDefaults,
                            new StateSourceOptions<AppSettings.Fragment> { Priority = 0 }
                        )
                    );
                });
            });
        });
        using var serviceProvider = services.BuildServiceProvider();

        var options = serviceProvider.GetRequiredService<IWritableState<AppSettings>>();
        var diagnostics = ((IConfiglueDiagnostics<AppSettings>)options).GetDiagnostics();
        (diagnostics.Sources.Count).ShouldBe(2);
        (
            diagnostics.Sources.Any(static source => source.Id == SourceId.From("user"))
        ).ShouldBeTrue();
        (
            diagnostics.Sources.Any(static source => source.Id == SourceId.From("defaults"))
        ).ShouldBeTrue();
        (diagnostics.DefaultWriteSourceId).ShouldBe(SourceId.From("user"));
        var check = ((IConfiglueDiagnostics<AppSettings>)options).Check();
        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }
        (await check.Result).IsResolved.ShouldBeTrue();
        (streamed.Count).ShouldBe(2);
        var initial = await options.GetValueAsync();
        var changed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = options.OnChange(value => changed.TrySetResult(value.RetryCount));
        await options.SaveAsync(patch => patch.RetryCount = 9);
        var savedUserState = await user.ReadAsync();
        var changedRetryCount = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (initial.RetryCount).ShouldBe(4);
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
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "profile";
                model.ConfigureSources(registration =>
                {
                    var resolved = registration.Services!.GetRequiredKeyedService<
                        InMemoryStateSource<AppSettings.Fragment>
                    >("profile-source");
                    registration.Sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "profile-source",
                            resolved,
                            new StateSourceOptions<AppSettings.Fragment> { Writer = resolved }
                        )
                    );
                });
            });
        });
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
            return ValueTaskCompat.FromResult(StateReadResult<T>.Success(value!, "reader-only"));
        }
    }

    private sealed class ReaderWithCapabilities(
        ISourceWriter<AppSettings.Fragment> writer,
        ISourceWatcher watcher
    ) : ISourceCapabilities<AppSettings.Fragment>
    {
        public ISourceWriter<AppSettings.Fragment>? Writer { get; } = writer;
        public ISourceWatcher? Watcher { get; } = watcher;

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => ValueTaskCompat.FromResult(StateReadResult<AppSettings.Fragment>.NotFound());
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
