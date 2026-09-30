using Configlue.Extensions.MSOptions;
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
            var context = ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model => model.Sources(sources => sources.Add(source)));
            })
        )
        {
            var value = await context.GetState<AppSettings>().GetValueAsync();
            (value.Label).ShouldBe("context-value");
        }

        ConfiglueApp.Initialize(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(CreateSource("static", "static-value")))
            );
        });
        try
        {
            var value = await ConfiglueApp.GetState<AppSettings>().GetValueAsync();
            (value.Label).ShouldBe("static-value");
            (await ConfiglueApp.GetState<AppSettings>().GetValueAsync()).Label.ShouldBe(
                "static-value"
            );

            Should.Throw<InvalidOperationException>(() => ConfiglueApp.Initialize(_ => { }));
        }
        finally
        {
            await ConfiglueApp.ShutdownAsync();
        }
    }

    [Test]
    public async Task RegistrationWritePlanCreatesSparseOverridesForOrdinaryEdits()
    {
        var overlay = new InMemoryStateSource<AppSettings.Fragment>(new AppSettings.Fragment());
        var sessionOverlay = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment()
        );
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
                Label = Optional<string?>.Present("default-label"),
            }
        );
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.WritePlan = new StateWritePlan(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["RetryCount"] = "user-overlay",
                    }
                );
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "user-overlay",
                            overlay,
                            priority: 100,
                            writer: overlay
                        )
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("defaults", defaults, priority: 0)
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "session-overlay",
                            sessionOverlay,
                            priority: 200,
                            writer: sessionOverlay
                        )
                    );
                });
            });
        });

        var options = context.GetRuntimeState<AppSettings>();
        (await options.GetValueAsync()).RetryCount.ShouldBe(3);
        using (var edit = await options.OpenEditSessionAsync())
        {
            edit.Value.RetryCount = 8;
            await edit.CommitAsync();
        }

        var overlayFragment = (await overlay.ReadAsync()).Value!;
        overlayFragment.RetryCount.Value.ShouldBe(8);
        overlayFragment.Label.IsPresent.ShouldBeFalse();
        var resolved = (await options.GetValueAsync());
        resolved.RetryCount.ShouldBe(8);
        resolved.Label.ShouldBe("default-label");

        using (
            var edit = await options.OpenEditSessionAsync(
                new StateWritePlan(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["RetryCount"] = "session-overlay",
                    }
                )
            )
        )
        {
            edit.Value.RetryCount = 9;
            await edit.CommitAsync();
        }
        (await sessionOverlay.ReadAsync()).Value!.RetryCount.Value.ShouldBe(9);
        (await options.GetValueAsync()).RetryCount.ShouldBe(9);
    }

    [Test]
    public async Task DiFacadeUsesSameRegistrationForConfiglueAndMicrosoftOptions()
    {
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(_ => CreateSource("di", "di-value")))
            );
        });

        await using var provider = services.BuildServiceProvider();
        var readOnly = provider.GetRequiredService<IReadOnlyState<AppSettings>>();
        var writable = provider.GetRequiredService<IWritableState<AppSettings>>();
        var value = await readOnly.GetValueAsync();

        (ReferenceEquals(readOnly, writable)).ShouldBeTrue();
        (value.Label).ShouldBe("di-value");
        (provider.GetRequiredService<IOptions<AppSettings>>().Value.Label).ShouldBe("di-value");
    }

    [Test]
    public async Task DiFacadeResolvesSourceConfigurationAfterStructuralRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SettingsSourceValue("from-provider"));
        var sourceConfigurationCalls = 0;
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.ConfigureSources(registration =>
                {
                    sourceConfigurationCalls++;
                    var sourceValue =
                        registration.Services!.GetRequiredService<SettingsSourceValue>();
                    registration.Sources.Add(CreateSource("provider-source", sourceValue.Value));
                })
            );
        });

        (sourceConfigurationCalls).ShouldBe(0);
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IReadOnlyState<AppSettings>>();

        (sourceConfigurationCalls).ShouldBe(1);
        (await options.GetValueAsync()).Label.ShouldBe("from-provider");
        (sourceConfigurationCalls).ShouldBe(1);
    }

    [Test]
    public async Task ProviderAwareSourcesSupportContextsWithoutDependencyInjection()
    {
        var receivedNullProvider = false;
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.ConfigureSources(registration =>
                {
                    receivedNullProvider = registration.Services is null;
                    registration.Sources.Add(
                        CreateSource("non-di-provider-source", "without-provider")
                    );
                })
            );
        });

        (receivedNullProvider).ShouldBeTrue();
        (await context.GetState<AppSettings>().GetValueAsync()).Label.ShouldBe("without-provider");
    }

    [Test]
    public async Task DiFacadeRegistersNamedInstancesAsKeyedAndMicrosoftOptions()
    {
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "profile";
                model.Sources(sources =>
                    sources.Add(_ => CreateSource("profile", "profile-value"))
                );
            });
        });

        await using var provider = services.BuildServiceProvider();
        var keyedOptions = provider.GetRequiredKeyedService<IReadOnlyState<AppSettings>>("profile");

        (await keyedOptions.GetValueAsync()).Label.ShouldBe("profile-value");
        (provider.GetRequiredService<IOptionsMonitor<AppSettings>>().Get("profile").Label).ShouldBe(
            "profile-value"
        );
    }

    [Test]
    public async Task ContextResolvesNamedModelInstances()
    {
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "first";
                model.Sources(sources => sources.Add(CreateSource("first", "first-value")));
            });
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "second";
                model.Sources(sources => sources.Add(CreateSource("second", "second-value")));
            });
        });

        (await context.GetState<AppSettings>("first").GetValueAsync()).Label.ShouldBe(
            "first-value"
        );
        (await context.GetState<AppSettings>("second").GetValueAsync()).Label.ShouldBe(
            "second-value"
        );
        Should.Throw<KeyNotFoundException>(() => context.GetState<AppSettings>());
    }

    [Test]
    public async Task ProfileCatalogReconciliationKeepsUnrelatedDynamicOptions()
    {
        var catalogStore = new InMemoryStateSource<ConfiglueProfileCatalog>(
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
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.ConfigureSources(registration =>
                    registration.Sources.Add(_ =>
                        CreateSource(
                            string.IsNullOrEmpty(registration.StateName)
                                ? "default-source"
                                : $"profile-{registration.StateName}",
                            $"{registration.StateName}-value"
                        )
                    )
                );
            });
        });

        var registry = context.GetStateRegistry<AppSettings>();
        registry.TryAdd("temporary").ShouldBeTrue();
        var profiles = context.GetProfiledState<AppSettings>();

        (await profiles.GetProfileNamesAsync()).ShouldContain("default");
        registry.TryGet("temporary", out var temporary).ShouldBeTrue();
        (await temporary!.GetValueAsync()).Label.ShouldBe("temporary-value");
    }

    [Test]
    public async Task FacadeProfilesCreateSwitchAndRemoveNamedOptionsInContext()
    {
        var catalogStore = new InMemoryStateSource<ConfiglueProfileCatalog>();
        var catalog = new StateSource<ConfiglueProfileCatalog>(
            "catalog",
            catalogStore,
            writer: catalogStore,
            watcher: catalogStore
        );
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.ConfigureSources(registration =>
                    registration.Sources.Add(_ =>
                        CreateSource(
                            string.IsNullOrEmpty(registration.StateName)
                                ? "fixed"
                                : $"profile-{registration.StateName}",
                            string.IsNullOrEmpty(registration.StateName)
                                ? "fixed-value"
                                : $"{registration.StateName}-value"
                        )
                    )
                );
            });
        });

        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.GetProfileNamesAsync();
        await profiles.CreateProfileAsync("Work", copyFrom: "default");
        (await context.GetState<AppSettings>("Work").GetValueAsync()).Label.ShouldBe(
            "default-value"
        );
        await profiles.SetActiveProfileAsync("Work");
        (await profiles.GetActiveValueAsync()).Label.ShouldBe("default-value");

        var removedHandle = context.GetState<AppSettings>("Work");
        await profiles.RemoveProfileAsync("Work");
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await removedHandle.GetValueAsync()
        );
        Should.Throw<KeyNotFoundException>(() => context.GetState<AppSettings>("Work"));
        (await context.GetState<AppSettings>("default").GetValueAsync()).Label.ShouldBe(
            "default-value"
        );
    }

    [Test]
    public async Task FacadeProfileRegistryEventsCanReenterManagerAfterCatalogChanges()
    {
        var catalogStore = new InMemoryStateSource<ConfiglueProfileCatalog>();
        var catalog = new StateSource<ConfiglueProfileCatalog>(
            "catalog",
            catalogStore,
            writer: catalogStore,
            watcher: catalogStore
        );
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.ConfigureSources(registration =>
                    registration.Sources.Add(_ =>
                        CreateSource(
                            string.IsNullOrEmpty(registration.StateName)
                                ? "default-source"
                                : $"profile-{registration.StateName}",
                            $"{registration.StateName}-value"
                        )
                    )
                );
            });
        });

        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.GetProfileNamesAsync();
        var registry = context.GetStateRegistry<AppSettings>();
        var addedObservation = new TaskCompletionSource<(bool IsPublished, string? Value)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.StateAdded += (name, options) =>
        {
            if (name != "Work")
            {
                return;
            }

            var names = profiles
                .GetProfileNamesAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            var value = options
                .GetValueAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            addedObservation.TrySetResult((names.Contains(name), value.Label));
        };

        await profiles
            .CreateProfileAsync("Work", copyFrom: "default")
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        (await addedObservation.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(
            (true, "default-value")
        );

        var removedObservation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.StateRemoved += name =>
        {
            if (name != "Work")
            {
                return;
            }

            var names = profiles
                .GetProfileNamesAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            removedObservation.TrySetResult(!names.Contains(name));
        };

        await profiles.RemoveProfileAsync("Work").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        (await removedObservation.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
    }

    [Test]
    public async Task FacadeDynamicOptionsUpdateDiMonitorAndInvalidateRemovedHandles()
    {
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableDynamicStates = true;
                model.ConfigureSources(registration =>
                    registration.Sources.Add(_ =>
                        CreateSource(
                            $"dynamic-{registration.StateName}",
                            $"{registration.StateName}-value"
                        )
                    )
                );
            });
        });

        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IConfiglueStateRegistry<AppSettings>>();
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
        var catalogStore = new InMemoryStateSource<ConfiglueProfileCatalog>();
        var catalog = new StateSource<ConfiglueProfileCatalog>(
            "catalog",
            catalogStore,
            writer: catalogStore,
            watcher: catalogStore
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.ConfigureSources(registration =>
                    registration.Sources.Add(_ =>
                        CreateSource(
                            $"di-profile-{registration.StateName}",
                            $"{registration.StateName}-value"
                        )
                    )
                );
            });
        });

        await using var provider = services.BuildServiceProvider();
        var profiles = provider.GetRequiredService<IConfiglueProfiledState<AppSettings>>();
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

    [Test]
    public async Task FacadeRegistryWaitsForOrderedConcurrentNotifications()
    {
        var registry = new ConfiglueFacadeStateRegistry<AppSettings>(
            name =>
            {
                var store = new InMemoryStateSource<AppSettings.Fragment>();
                var source = new StateSource<AppSettings.Fragment>(
                    $"facade-{name}",
                    store,
                    writer: store,
                    watcher: store
                );
                var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                    new StateSourceSet<AppSettings.Fragment>([source])
                );
                return (runtime, []);
            },
            reservedNames: []
        );
        var added = new List<string>();
        var firstAdded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirstAdded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.StateAdded += (name, _) =>
        {
            if (name == "first")
            {
                firstAdded.TrySetResult();
                releaseFirstAdded.Task.GetAwaiter().GetResult();
            }
            added.Add(name);
        };

        var firstAdd = Task.Run(() => registry.TryAdd("first"));
        await firstAdded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondAdd = Task.Run(() => registry.TryAdd("second"));
        try
        {
            (
                await Task.Run(() =>
                    SpinWait.SpinUntil(
                        () => registry.TryGet("second", out _),
                        TimeSpan.FromSeconds(5)
                    )
                )
            ).ShouldBeTrue();
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            (secondAdd.IsCompleted).ShouldBeFalse();
        }
        finally
        {
            releaseFirstAdded.TrySetResult();
        }

        (await Task.WhenAll(firstAdd, secondAdd)).ShouldBe(new[] { true, true });
        (added).ShouldBe(new[] { "first", "second" });

        var firstRemoved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirstRemoved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.StateRemoved += name =>
        {
            if (name == "first")
            {
                firstRemoved.TrySetResult();
                releaseFirstRemoved.Task.GetAwaiter().GetResult();
            }
        };

        var firstRemove = Task.Run(() => registry.TryRemoveAsync("first").AsTask());
        await firstRemoved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clear = Task.Run(() => registry.ClearAsync().AsTask());
        try
        {
            (
                await Task.Run(() =>
                    SpinWait.SpinUntil(
                        () => registry.StateNames.Count == 0,
                        TimeSpan.FromSeconds(5)
                    )
                )
            ).ShouldBeTrue();
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            (clear.IsCompleted).ShouldBeFalse();
        }
        finally
        {
            releaseFirstRemoved.TrySetResult();
        }

        await Task.WhenAll(firstRemove, clear);

        registry.TryAdd("reentrant").ShouldBeTrue();
        registry.StateRemoved += name =>
        {
            if (name == "reentrant")
            {
                registry.Clear();
            }
        };
        (
            await Task.Run(() => registry.TryRemove("reentrant")).WaitAsync(TimeSpan.FromSeconds(5))
        ).ShouldBeTrue();

        registry.TryAdd("dispose").ShouldBeTrue();
        var disposeNotification = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseDisposeNotification = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.StateRemoved += name =>
        {
            if (name == "dispose")
            {
                disposeNotification.TrySetResult();
                releaseDisposeNotification.Task.GetAwaiter().GetResult();
            }
        };

        var dispose = Task.Run(async () => await registry.DisposeAsync());
        try
        {
            await disposeNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            (dispose.IsCompleted).ShouldBeFalse();
        }
        finally
        {
            releaseDisposeNotification.TrySetResult();
        }

        await dispose;
    }

    private static StateSource<AppSettings.Fragment> CreateSource(string id, string label)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present(label) }
        );
        return new StateSource<AppSettings.Fragment>(id, store, writer: store, watcher: store);
    }

    private sealed record SettingsSourceValue
    {
        public string Value { get; init; }

        public SettingsSourceValue(string Value)
        {
            this.Value = Value;
        }

        public void Deconstruct(out string Value)
        {
            Value = this.Value;
        }
    }
}
