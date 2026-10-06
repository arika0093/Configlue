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
                        new StateSource<AppSettings.Fragment>(
                            "user-overlay",
                            overlay,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Priority = 100,
                                Writer = overlay,
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
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "session-overlay",
                            sessionOverlay,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Priority = 200,
                                Writer = sessionOverlay,
                            }
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
    public async Task ProfileCatalogKeepsUnrelatedDynamicOptions()
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
            new StateSourceOptions<ConfiglueProfileCatalog>
            {
                Writer = catalogStore,
                Watcher = catalogStore,
            }
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
        var catalog = new StateSource<ConfiglueProfileCatalog>(
            "catalog",
            catalogStore,
            new StateSourceOptions<ConfiglueProfileCatalog>
            {
                Writer = catalogStore,
                Watcher = catalogStore,
            }
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
    public async Task FacadeProfileRegistryEventsObserveCreatedAndRemovedProfiles()
    {
        var catalogStore = new InMemoryStateSource<ConfiglueProfileCatalog>();
        var catalog = new StateSource<ConfiglueProfileCatalog>(
            "catalog",
            catalogStore,
            new StateSourceOptions<ConfiglueProfileCatalog>
            {
                Writer = catalogStore,
                Watcher = catalogStore,
            }
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
        // Registry listeners observe transitions but must not synchronously wait for
        // profile operations: profile mutations hold their own gate while registry
        // events dispatch synchronously.
        var addedObservation = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.StateAdded += (name, _) =>
        {
            if (name == "Work")
            {
                addedObservation.TrySetResult(name);
            }
        };
        var removedObservation = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.StateRemoved += name =>
        {
            if (name == "Work")
            {
                removedObservation.TrySetResult(name);
            }
        };

        await profiles.CreateProfileAsync("Work", copyFrom: "default");
        (await addedObservation.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("Work");
        (await profiles.GetProfileNamesAsync()).ShouldContain("Work");

        await profiles.RemoveProfileAsync("Work");
        (await removedObservation.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("Work");
        (await profiles.GetProfileNamesAsync()).ShouldNotContain("Work");
    }

    [Test]
    public async Task FacadeRegistryWaitsForOrderedConcurrentNotifications()
    {
        var registry = new ConfiglueOwnedStateRegistry<AppSettings>(
            name =>
            {
                var store = new InMemoryStateSource<AppSettings.Fragment>();
                var source = new StateSource<AppSettings.Fragment>(
                    $"facade-{name}",
                    store,
                    new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }
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

        var firstAdd = Task
            .Factory.StartNew(
                async () => await registry.TryAddAsync("first"),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            )
            .Unwrap();
        await firstAdded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondAdd = Task
            .Factory.StartNew(
                async () => await registry.TryAddAsync("second"),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            )
            .Unwrap();
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
    public async Task FacadeDisposeAsyncDisposesOwnedResourcesAndRemainsIdempotent(
        bool cleanupFails
    )
    {
        var gate = new DisposalGate { FailOnRelease = cleanupFails };
        const string stateName = "dispose-owned";
        var registry = CreateGatedRegistry(gate);
        (await registry.TryAddAsync(stateName)).ShouldBeTrue();

        var removed = new List<string>();
        registry.StateRemoved += name =>
        {
            if (name == stateName)
            {
                removed.Add(name);
            }
        };

        var disposeTask = registry.DisposeAsync().AsTask();
        (disposeTask.IsCompleted).ShouldBeFalse();
        gate.Release();

        var repeatedDispose = registry.DisposeAsync().AsTask();
        (ReferenceEquals(repeatedDispose, disposeTask)).ShouldBeTrue();

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

        (removed).ShouldBe(new[] { stateName });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposalStartedInsideNotification_ExternalDisposeDoesNotWaitForInFlightNotification(
        bool useCoreRegistry
    )
    {
        // Simplified registry contract (#284, see IConfiglueStateRegistry remarks):
        // each operation delivers only its own transitions and never waits for
        // another operation's callbacks; DisposeAsync neither waits for in-flight
        // notifications nor aggregates concurrent operations, and returns the same
        // task once disposal has started. The inner dispose therefore completes
        // (including its own StateRemoved delivery) before the blocked StateAdded
        // handler signals, and the external dispose observes that completed task
        // instead of waiting for the in-flight notification.
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableDynamicStates = true;
                model.ConfigureSources(registration =>
                {
                    var store = new InMemoryStateSource<AppSettings.Fragment>();
                    var sourceId = string.IsNullOrEmpty(registration.StateName)
                        ? "default"
                        : registration.StateName;
                    registration.Sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            sourceId,
                            store,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Writer = store,
                                Watcher = store,
                            }
                        )
                    );
                });
            });
        });
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
            external.IsCompleted.ShouldBeTrue();
            removed.ShouldBeTrue();
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

    [Test]
    public async Task FacadeRegistryBlockingHandlerKeepsMutationVisibleButSerializesCompletion()
    {
        var registry = CreateFacadeRegistry();
        var added = new List<string>();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.StateAdded += (name, _) =>
        {
            if (name == "first")
            {
                firstEntered.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
            }
            lock (added)
            {
                added.Add(name);
            }
        };

        var firstAdd = Task.Run(() => registry.TryAddAsync("first").AsTask());
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        (firstAdd.IsCompleted).ShouldBeFalse();
        registry.TryGet("first", out _).ShouldBeTrue();

        var secondAdd = Task.Run(() => registry.TryAddAsync("second").AsTask());
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

        releaseFirst.TrySetResult();
        (await Task.WhenAll(firstAdd, secondAdd)).ShouldBe(new[] { true, true });
        lock (added)
        {
            (added).ShouldBe(new[] { "first", "second" });
        }

        await registry.DisposeAsync();
    }

    [Test]
    public async Task FacadeRegistryConcurrentRemoveAndClearDisposeExactlyOnceWithoutMissingNotifications()
    {
        var disposeCounts = new System.Collections.Concurrent.ConcurrentDictionary<
            string,
            int
        >(StringComparer.Ordinal);
        var registry = new ConfiglueOwnedStateRegistry<AppSettings>(
            name =>
            {
                var store = new InMemoryStateSource<AppSettings.Fragment>();
                var source = new StateSource<AppSettings.Fragment>(
                    $"facade-{name}",
                    store,
                    new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }
                );
                var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                    new StateSourceSet<AppSettings.Fragment>([source])
                );
                return (
                    (IWritableState<AppSettings>)runtime,
                    new object[] { new CountingDisposable(name, disposeCounts) }
                );
            },
            reservedNames: []
        );
        (await registry.TryAddAsync("a")).ShouldBeTrue();
        (await registry.TryAddAsync("b")).ShouldBeTrue();

        var removed = new List<string>();
        registry.StateRemoved += name =>
        {
            lock (removed)
            {
                removed.Add(name);
            }
        };

        var removeA = Task.Run(() => registry.TryRemoveAsync("a").AsTask());
        var clear = Task.Run(() => registry.ClearAsync().AsTask());
        await Task.WhenAll(removeA, clear).WaitAsync(TimeSpan.FromSeconds(5));

        (registry.StateNames.Count).ShouldBe(0);
        registry.TryGet("a", out _).ShouldBeFalse();
        registry.TryGet("b", out _).ShouldBeFalse();
        List<string> removedSnapshot;
        lock (removed)
        {
            removedSnapshot = removed.ToList();
        }
        (removedSnapshot.OrderBy(static name => name)).ShouldBe(new[] { "a", "b" });
        (disposeCounts.GetValueOrDefault("a", 0)).ShouldBe(1);
        (disposeCounts.GetValueOrDefault("b", 0)).ShouldBe(1);

        await registry.DisposeAsync();
        lock (removed)
        {
            (removed.Count).ShouldBe(2);
        }
    }

    [Test]
    public async Task FacadeRegistryClearDoesNotAggregateInFlightRemovalFailure()
    {
        var failingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFailing = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var registry = new ConfiglueOwnedStateRegistry<AppSettings>(
            name =>
            {
                var store = new InMemoryStateSource<AppSettings.Fragment>();
                var source = new StateSource<AppSettings.Fragment>(
                    $"facade-{name}",
                    store,
                    new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }
                );
                var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                    new StateSourceSet<AppSettings.Fragment>([source])
                );
                object[] resources =
                    name == "failing"
                        ? [
                            new GatedThrowingResource(
                                failingStarted,
                                releaseFailing,
                                new InvalidOperationException("failing disposal failed.")
                            ),
                        ]
                        : [];
                return ((IWritableState<AppSettings>)runtime, resources);
            },
            reservedNames: []
        );
        (await registry.TryAddAsync("failing")).ShouldBeTrue();
        (await registry.TryAddAsync("other")).ShouldBeTrue();

        var removed = new List<string>();
        registry.StateRemoved += name =>
        {
            lock (removed)
            {
                removed.Add(name);
            }
        };

        // Start the removal inline (no Task.Run): TryRemoveAsync retires the
        // entry synchronously before its first await, so TryGet is false
        // deterministically without depending on thread-pool scheduling.
        // The previous Task.Run + blocking SpinWait flaked on windows-latest
        // when the pool was saturated.
        var pendingRemove = registry.TryRemoveAsync("failing").AsTask();
        var retireDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (registry.TryGet("failing", out _))
        {
            if (DateTime.UtcNow >= retireDeadline)
            {
                break;
            }

            await Task.Delay(10);
        }

        (!registry.TryGet("failing", out _)).ShouldBeTrue();
        await failingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await registry.ClearAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        List<string> afterClear;
        lock (removed)
        {
            afterClear = removed.ToList();
        }
        (afterClear).ShouldBe(new[] { "other" });

        releaseFailing.TrySetResult();
        var failure = await Should.ThrowAsync<AggregateException>(async () =>
            await pendingRemove.WaitAsync(TimeSpan.FromSeconds(5))
        );
        (failure.InnerExceptions.OfType<InvalidOperationException>().Count()).ShouldBe(1);

        List<string> afterRemove;
        lock (removed)
        {
            afterRemove = removed.ToList();
        }
        (afterRemove.OrderBy(static name => name)).ShouldBe(new[] { "failing", "other" });

        await registry.DisposeAsync();
    }

    [Test]
    public async Task FacadeRegistryClearReportsOwnFailuresWithUnifiedMessage()
    {
        var registry = new ConfiglueOwnedStateRegistry<AppSettings>(
            name =>
            {
                var store = new InMemoryStateSource<AppSettings.Fragment>();
                var source = new StateSource<AppSettings.Fragment>(
                    $"facade-{name}",
                    store,
                    new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }
                );
                var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                    new StateSourceSet<AppSettings.Fragment>([source])
                );
                object[] resources =
                    name == "bad"
                        ? [new ThrowingAsyncResource(new InvalidOperationException("bad broke."))]
                        : [];
                return ((IWritableState<AppSettings>)runtime, resources);
            },
            reservedNames: []
        );
        (await registry.TryAddAsync("bad")).ShouldBeTrue();

        var failure = await Should.ThrowAsync<AggregateException>(async () =>
            await registry.ClearAsync()
        );
        (failure.Message.StartsWith("One or more Configlue states failed to clear.", StringComparison.Ordinal)).ShouldBeTrue();

        await registry.DisposeAsync();
    }

    [Test]
    public async Task FacadeRegistryDeferredDisposeCompletesBeforeNotifications()
    {
        var registry = CreateFacadeRegistry();
        (await registry.TryAddAsync("deferred")).ShouldBeTrue();

        var deferrer = (IConfiglueStateRegistryNotificationDeferrer<AppSettings>)registry;
        using var scope = deferrer.DeferNotifications();

        var removed = new List<string>();
        registry.StateRemoved += name =>
        {
            lock (removed)
            {
                removed.Add(name);
            }
        };

        var disposeTask = registry.DisposeAsync().AsTask();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Should.Throw<ObjectDisposedException>(() => registry.StateNames);
        Should.Throw<ObjectDisposedException>(() => registry.Get("deferred"));
        lock (removed)
        {
            (removed).ShouldBeEmpty();
        }

        scope.Dispose();

        List<string> afterRelease;
        lock (removed)
        {
            afterRelease = removed.ToList();
        }
        (afterRelease).ShouldBe(new[] { "deferred" });
        (ReferenceEquals(registry.DisposeAsync().AsTask(), disposeTask)).ShouldBeTrue();
    }

    private static ConfiglueOwnedStateRegistry<AppSettings> CreateFacadeRegistry() =>
        CreateGatedRegistry(gate: null);

    private static ConfiglueOwnedStateRegistry<AppSettings> CreateGatedRegistry(
        DisposalGate? gate
    ) =>
        new(
            name =>
            {
                var store = new InMemoryStateSource<AppSettings.Fragment>();
                var source = new StateSource<AppSettings.Fragment>(
                    $"facade-{name}",
                    store,
                    new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }
                );
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

    private sealed class CountingDisposable(
        string name,
        System.Collections.Concurrent.ConcurrentDictionary<string, int> counts
    ) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            counts.AddOrUpdate(name, 1, (_, current) => current + 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedThrowingResource(
        TaskCompletionSource started,
        TaskCompletionSource release,
        Exception failure
    ) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            throw failure;
        }
    }

    private sealed class ThrowingAsyncResource(Exception failure) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(failure);
    }

    private static StateSource<AppSettings.Fragment> CreateSource(string id, string label)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present(label) }
        );
        return new StateSource<AppSettings.Fragment>(
            id,
            store,
            new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }
        );
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
