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
                    SourceId.From("user-overlay"),
                    new Dictionary<string, SourceId>(StringComparer.Ordinal)
                    {
                        ["RetryCount"] = SourceId.From("user-overlay"),
                    }
                );
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("user-overlay", overlay, new StateSourceOptions<AppSettings.Fragment> { Priority = 100, Writer = overlay })
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("defaults", defaults, new StateSourceOptions<AppSettings.Fragment> { Priority = 0 })
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("session-overlay", sessionOverlay, new StateSourceOptions<AppSettings.Fragment> { Priority = 200, Writer = sessionOverlay })
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
        var catalog = new StateSource<ConfiglueProfileCatalog>("catalog", catalogStore, new StateSourceOptions<ConfiglueProfileCatalog> { Writer = catalogStore, Watcher = catalogStore });
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
        (await registry.TryAddAsync("temporary")).ShouldBeTrue();
        var profiles = context.GetProfiledState<AppSettings>();

        (await profiles.GetProfileNamesAsync()).ShouldContain("default");
        registry.TryGet("temporary", out var temporary).ShouldBeTrue();
        (await temporary!.GetValueAsync()).Label.ShouldBe("temporary-value");
    }

    [Test]
    public async Task FacadeProfilesCreateSwitchAndRemoveNamedOptionsInContext()
    {
        var catalogStore = new InMemoryStateSource<ConfiglueProfileCatalog>();
        var catalog = new StateSource<ConfiglueProfileCatalog>("catalog", catalogStore, new StateSourceOptions<ConfiglueProfileCatalog> { Writer = catalogStore, Watcher = catalogStore });
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
        var catalog = new StateSource<ConfiglueProfileCatalog>("catalog", catalogStore, new StateSourceOptions<ConfiglueProfileCatalog> { Writer = catalogStore, Watcher = catalogStore });
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
        (await registry.TryAddAsync("late")).ShouldBeTrue();
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
        var catalog = new StateSource<ConfiglueProfileCatalog>("catalog", catalogStore, new StateSourceOptions<ConfiglueProfileCatalog> { Writer = catalogStore, Watcher = catalogStore });
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
                var source = new StateSource<AppSettings.Fragment>($"facade-{name}", store, new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store });
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

        var firstAdd = Task.Factory.StartNew(
            async () => await registry.TryAddAsync("first"),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        ).Unwrap();
        await firstAdded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondAdd = Task.Factory.StartNew(
            async () => await registry.TryAddAsync("second"),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        ).Unwrap();
        try
        {
            var visibilityDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!registry.TryGet("second", out _) && DateTime.UtcNow < visibilityDeadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
            registry.TryGet("second", out _).ShouldBeTrue();
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

        (await registry.TryAddAsync("reentrant")).ShouldBeTrue();
        registry.StateRemoved += name =>
        {
            if (name == "reentrant")
            {
                _ = registry.ClearAsync();
            }
        };
        (
            await Task.Run(async () => await registry.TryRemoveAsync("reentrant"))
                .WaitAsync(TimeSpan.FromSeconds(5))
        ).ShouldBeTrue();

        (await registry.TryAddAsync("dispose")).ShouldBeTrue();
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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FacadeDisposeAsyncCompletesOnlyAfterOwnedStateRemovedNotificationDrains(
        bool cleanupFails
    )
    {
        var gate = new DisposalGate { FailOnRelease = cleanupFails };
        const string stateName = "dispose-owned";
        var registry = CreateGatedRegistry(gate);
        (await registry.TryAddAsync(stateName)).ShouldBeTrue();

        var notificationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseNotification = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var completionObservedDuringNotification = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var disposeTaskHolder = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.StateRemoved += name =>
        {
            if (name != stateName)
            {
                return;
            }

            completionObservedDuringNotification.TrySetResult(
                disposeTaskHolder.Task.GetAwaiter().GetResult().IsCompleted
            );
            notificationStarted.TrySetResult();
            releaseNotification.Task.GetAwaiter().GetResult();
        };

        var disposeTask = registry.DisposeAsync().AsTask();
        disposeTaskHolder.TrySetResult(disposeTask);

        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (disposeTask.IsCompleted).ShouldBeFalse();
        gate.Release();

        await notificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (
            await completionObservedDuringNotification.Task.WaitAsync(TimeSpan.FromSeconds(5))
        ).ShouldBeFalse();
        (disposeTask.IsCompleted).ShouldBeFalse();

        var repeatedDispose = registry.DisposeAsync().AsTask();
        (ReferenceEquals(repeatedDispose, disposeTask)).ShouldBeTrue();

        releaseNotification.TrySetResult();
        if (cleanupFails)
        {
            await Should.ThrowAsync<AggregateException>(async () =>
                await disposeTask.WaitAsync(TimeSpan.FromSeconds(5))
            );
            await Should.ThrowAsync<AggregateException>(async () => await registry.DisposeAsync());
        }
        else
        {
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposalStartedInsideNotification_ExternalWaitIncludesThatNotification(
        bool useCoreRegistry
    )
    {
        var services = new ServiceCollection();
        services.AddConfiglueStateRegistry<AppSettings, AppSettings.Fragment>(
            (_, name) =>
            {
                var store = new InMemoryStateSource<AppSettings.Fragment>();
                return new StateSourceSet<AppSettings.Fragment>([
                    new StateSource<AppSettings.Fragment>(name, store, new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }),
                ]);
            }
        );
        await using var provider = services.BuildServiceProvider();
        IConfiglueStateRegistry<AppSettings> registry = useCoreRegistry
            ? provider.GetRequiredService<IConfiglueStateRegistry<AppSettings>>()
            : CreateFacadeRegistry();
        var reentrantCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = false;
        registry.StateAdded += (_, _) =>
        {
            registry.DisposeAsync().GetAwaiter().GetResult();
            reentrantCompleted.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        registry.StateRemoved += _ => removed = true;
        var adding = Task.Run(async () => await registry.TryAddAsync("reentrant-origin"));
        try
        {
            await reentrantCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var external = registry.DisposeAsync().AsTask();
            external.IsCompleted.ShouldBeFalse();
            removed.ShouldBeFalse();
            release.SetResult();
            await external.WaitAsync(TimeSpan.FromSeconds(5));
            removed.ShouldBeTrue();
        }
        finally
        {
            release.TrySetResult();
            await adding.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task FacadeDisposeAsyncIsDeadlockFreeWhenNotificationReentersDisposal()
    {
        var registry = CreateFacadeRegistry();
        (await registry.TryAddAsync("reentrant")).ShouldBeTrue();
        var reentered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var reentrantCompletedSynchronously = false;
        registry.StateRemoved += name =>
        {
            if (name != "reentrant")
            {
                return;
            }

            var nested = registry.DisposeAsync();
            reentrantCompletedSynchronously = nested.IsCompletedSuccessfully;
            reentered.TrySetResult();
        };

        await Task.Run(async () => await registry.DisposeAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));
        await reentered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (reentrantCompletedSynchronously).ShouldBeTrue();
    }

    [Test]
    public async Task FacadeDisposeAsyncContinuesAfterRemovalNotificationFailure()
    {
        var registry = CreateFacadeRegistry();
        (await registry.TryAddAsync("failure")).ShouldBeTrue();
        var laterListenerCalled = false;
        registry.StateRemoved += _ => throw new InvalidOperationException("listener failure");
        registry.StateRemoved += name =>
        {
            if (name == "failure")
            {
                laterListenerCalled = true;
            }
        };

        await registry.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        (laterListenerCalled).ShouldBeTrue();
    }

    private static ConfiglueFacadeStateRegistry<AppSettings> CreateFacadeRegistry() =>
        CreateGatedRegistry(gate: null);

    private static ConfiglueFacadeStateRegistry<AppSettings> CreateGatedRegistry(
        DisposalGate? gate
    ) =>
        new(
            name =>
            {
                var store = new InMemoryStateSource<AppSettings.Fragment>();
                var source = new StateSource<AppSettings.Fragment>($"facade-{name}", store, new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store });
                var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                    new StateSourceSet<AppSettings.Fragment>([source])
                );
                return (runtime, gate is null ? [] : [gate]);
            },
            reservedNames: []
        );

    private sealed class DisposalGate : IAsyncDisposable
    {
        public bool FailOnRelease { get; init; }
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            if (FailOnRelease)
                throw new InvalidOperationException("Resource disposal failed.");
        }
    }

    private static StateSource<AppSettings.Fragment> CreateSource(string id, string label)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present(label) }
        );
        return new StateSource<AppSettings.Fragment>(id, store, new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store });
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
