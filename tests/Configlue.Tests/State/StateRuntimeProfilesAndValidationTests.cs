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
    public async Task Options_MigratesEachSourceFragmentBeforeMerging()
    {
        var oldSchema = new StateSchemaMetadata("app-settings", 1);
        var reader = new FixedStateReader<AppSettings.Fragment>(
            StateReadResult<AppSettings.Fragment>.Success(
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) },
                "revision-1",
                oldSchema
            )
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([new("legacy", reader)]);
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            sourceSet,
            migrations: [new AppSettingsV1ToV2Migration()]
        );

        var result = await options.ReadAsync();

        (result.Status).ShouldBe(StateReadStatus.Success);
        (result.Value!.RetryCount).ShouldBe(9);
        (result.Value.Label).ShouldBe("migrated");
        (result.Schema).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
    }

    [Test]
    public async Task Options_ValidatesBeforeSavingAndLeavesTheStoredRevisionUntouched()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("user", store, writer: store),
        ]);
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueValidator<AppSettings>(new RetryCountValidator());
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            sourceSet,
            validateDataAnnotations: true
        );
        await using var serviceProvider = services.BuildServiceProvider();
        var writable = serviceProvider.GetRequiredService<IWritableState<AppSettings>>();
        ConfiglueValidationException? validationFailure = null;

        try
        {
            await writable.SaveAsync(patch => patch.RetryCount = 101);
        }
        catch (ConfiglueValidationException exception)
        {
            validationFailure = exception;
        }

        var stored = await store.ReadAsync();
        (validationFailure).ShouldNotBeNull();
        (validationFailure!.Failures.Count()).ShouldBe(2);
        (
            validationFailure.Failures.Any(failure =>
                failure.Contains("custom retry limit", StringComparison.Ordinal)
            )
        ).ShouldBeTrue();
        (stored.Value!.RetryCount.Value).ShouldBe(3);
        (stored.Revision).ShouldBe("1");

        var advanced =
            (IConfiglueRuntimeState<AppSettings>)
                serviceProvider.GetRequiredService<IWritableState<AppSettings>>();
        using var edit = await advanced.OpenEditSessionAsync();
        edit.Value.RetryCount = 101;
        var editWasRejected = false;
        try
        {
            await edit.CommitAsync();
        }
        catch (ConfiglueValidationException)
        {
            editWasRejected = true;
        }

        edit.Value.RetryCount = 4;
        await edit.CommitAsync();
        var validStored = await store.ReadAsync();
        (editWasRejected).ShouldBeTrue();
        (edit.IsCommitted).ShouldBeTrue();
        (validStored.Value!.RetryCount.Value).ShouldBe(4);
    }

    [Test]
    public async Task NamedAndRuntimeProfiles_PassTheirNamesToMicrosoftValidators()
    {
        var defaultStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var keyedStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var runtimeStores = new Dictionary<string, InMemoryStateSource<AppSettings.Fragment>>(
            StringComparer.Ordinal
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueValidator(new ProfileScopedRetryCountValidator());
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("default", defaultStore, writer: defaultStore),
            ])
        );
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            "custom",
            new StateSourceSet<AppSettings.Fragment>([
                new("custom", keyedStore, writer: keyedStore),
            ])
        );
        services.AddConfiglueStateRegistry<AppSettings, AppSettings.Fragment>(
            (_, profileName) =>
            {
                var store = new InMemoryStateSource<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
                );
                runtimeStores.Add(profileName, store);
                return new StateSourceSet<AppSettings.Fragment>([
                    new(profileName, store, writer: store),
                ]);
            }
        );
        await using var serviceProvider = services.BuildServiceProvider();

        var defaultOptions = serviceProvider.GetRequiredService<IWritableState<AppSettings>>();
        await defaultOptions.SaveAsync(patch => patch.RetryCount = 12);
        ((await defaultStore.ReadAsync()).Value!.RetryCount.Value).ShouldBe(12);

        var keyedOptions = serviceProvider.GetRequiredKeyedService<IWritableState<AppSettings>>(
            "custom"
        );
        var keyedBefore = await keyedStore.ReadAsync();
        var keyedFailure = await SaveInvalidAndCaptureAsync(keyedOptions);
        var keyedAfter = await keyedStore.ReadAsync();

        (keyedFailure.StateName).ShouldBe("custom");
        (keyedFailure.Failures).ShouldContain("RetryCount is too high for this profile.");
        (keyedAfter.Revision).ShouldBe(keyedBefore.Revision);
        (keyedAfter.Value!.RetryCount.Value).ShouldBe(3);

        var registry = serviceProvider.GetRequiredService<IConfiglueStateRegistry<AppSettings>>();
        (await registry.TryAddAsync("runtime")).ShouldBeTrue();
        var runtimeOptions = registry.Get("runtime");
        var runtimeBefore = await runtimeStores["runtime"].ReadAsync();
        var runtimeFailure = await SaveInvalidAndCaptureAsync(runtimeOptions);
        var runtimeAfter = await runtimeStores["runtime"].ReadAsync();

        (runtimeFailure.StateName).ShouldBe("runtime");
        (runtimeFailure.Failures).ShouldContain("RetryCount is too high for this profile.");
        (runtimeAfter.Revision).ShouldBe(runtimeBefore.Revision);
        (runtimeAfter.Value!.RetryCount.Value).ShouldBe(3);
    }

    [Test]
    public async Task SaveAsync_ChangesOnlyTheTargetContributionAndUnsetRevealsLowerValues()
    {
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
                Label = Optional<string?>.Present("default label"),
            }
        );
        var user = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Enabled = Optional<bool>.Present(false),
                RetryCount = Optional<int>.Present(9),
                Label = Optional<string?>.Present("old label"),
            }
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults, priority: 0),
        ]);
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(sourceSet);
        var patch = new AppSettings.Patch
        {
            RetryCount = FragmentOperation<int>.Unset,
            Label = FragmentOperation<string?>.Set("patched label"),
        };

        var write = await options.SaveAsync(patch);
        var stored = await user.ReadAsync();
        var resolved = await options.ReadAsync();

        (write.Revision).ShouldBe("2");
        (stored.Value!.Enabled.IsPresent).ShouldBeTrue();
        (stored.Value.RetryCount.IsPresent).ShouldBeFalse();
        (stored.Value.Label.Value).ShouldBe("patched label");
        (resolved.Value!.Enabled).ShouldBeFalse();
        (resolved.Value.RetryCount).ShouldBe(3);
        (resolved.Value.Label).ShouldBe("patched label");
    }

    [Test]
    public async Task SaveAsync_UnsetRevealsTheModelDefaultsContribution()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) }
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)])
        );

        await options.SaveAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Unset }
        );
        var resolved = await options.ReadAsync();
        var details = await options.GetDetailsAsync();

        (resolved.Value!.RetryCount).ShouldBe(3);
        (details.RetryCount.Source?.Kind).ShouldBe("model-defaults");
    }

    [Test]
    public async Task DependencyInjection_ResolvesNamedProfilesByServiceKey()
    {
        var primaryStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) }
        );
        var secondaryStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
        );
        var primarySources = new StateSourceSet<AppSettings.Fragment>([
            new("profile", primaryStore),
        ]);
        var secondarySources = new StateSourceSet<AppSettings.Fragment>([
            new("profile", secondaryStore),
        ]);
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>("primary", primarySources);
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            "secondary",
            secondarySources
        );
        await using var serviceProvider = services.BuildServiceProvider();
        var primary = serviceProvider.GetRequiredKeyedService<IReadOnlyState<AppSettings>>(
            "primary"
        );
        var secondary = serviceProvider.GetRequiredKeyedService<IReadOnlyState<AppSettings>>(
            "secondary"
        );

        var primaryValue = await primary.GetValueAsync();
        var secondaryValue = await secondary.GetValueAsync();

        (primaryValue.RetryCount).ShouldBe(5);
        (secondaryValue.RetryCount).ShouldBe(8);
    }

    [Test]
    public async Task DependencyInjection_ManagesDynamicProfilesThroughRegistry()
    {
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        var factoryCalls = 0;
        services.AddConfiglueStateRegistry<AppSettings, AppSettings.Fragment>(
            (_, profileName) =>
            {
                Interlocked.Increment(ref factoryCalls);
                var store = new InMemoryStateSource<AppSettings.Fragment>(
                    new AppSettings.Fragment
                    {
                        RetryCount = Optional<int>.Present(profileName == "primary" ? 5 : 8),
                    }
                );
                return new StateSourceSet<AppSettings.Fragment>([new("profile", store)]);
            }
        );
        await using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IConfiglueStateRegistry<AppSettings>>();
        var added = new List<string>();
        var removed = new List<string>();
        registry.StateAdded += (name, _) => added.Add(name);
        registry.StateRemoved += name => removed.Add(name);

        var addResults = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => registry.TryAddAsync("primary").AsTask())
        );
        await registry.TryAddAsync("secondary");
        var primary = await registry.Get("primary").GetValueAsync();
        var secondary = await registry.Get("secondary").GetValueAsync();
        var removedPrimary = await registry.TryRemoveAsync("primary");
        var removedAgain = await registry.TryRemoveAsync("primary");

        (addResults.Count(static result => result)).ShouldBe(1);
        (factoryCalls).ShouldBe(2);
        (primary.RetryCount).ShouldBe(5);
        (secondary.RetryCount).ShouldBe(8);
        ((registry.StateNames))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "secondary" }).OrderBy(static item => item));
        ((added))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "primary", "secondary" }).OrderBy(static item => item));
        ((removed))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "primary" }).OrderBy(static item => item));
        (removedPrimary).ShouldBeTrue();
        (removedAgain).ShouldBeFalse();
    }

    [Test]
    public async Task RegistryAddClearAndDisposeWaitForQueuedNotifications()
    {
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueStateRegistry<AppSettings, AppSettings.Fragment>(
            (_, name) =>
            {
                var store = new InMemoryStateSource<AppSettings.Fragment>();
                return new StateSourceSet<AppSettings.Fragment>([
                    new(name, store, writer: store, watcher: store),
                ]);
            }
        );
        await using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IConfiglueStateRegistry<AppSettings>>();
        var firstAdded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirstAdded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var addOrder = new List<string>();
        registry.StateAdded += (name, _) =>
        {
            if (name == "first")
            {
                firstAdded.TrySetResult();
                releaseFirstAdded.Task.GetAwaiter().GetResult();
            }
            addOrder.Add(name);
        };

        var firstAdd = Task.Run(async () => await registry.TryAddAsync("first"));
        await firstAdded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondAdd = Task.Run(async () => await registry.TryAddAsync("second"));
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
        (addOrder).ShouldBe(new[] { "first", "second" });

        var firstRemoved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirstRemoved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var removedNames = new List<string>();
        registry.StateRemoved += name =>
        {
            if (name == "first")
            {
                firstRemoved.TrySetResult();
                releaseFirstRemoved.Task.GetAwaiter().GetResult();
            }
            removedNames.Add(name);
        };

        var removeFirst = Task.Run(async () => await registry.TryRemoveAsync("first"));
        await firstRemoved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clear = Task.Run(async () => await registry.ClearAsync());
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

        await Task.WhenAll(removeFirst, clear);
        (removedNames).ShouldBe(new[] { "first", "second" });

        (await registry.TryAddAsync("dispose")).ShouldBeTrue();
        var disposedNameRemoved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseDisposeNotification = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.StateRemoved += name =>
        {
            if (name == "dispose")
            {
                disposedNameRemoved.TrySetResult();
                releaseDisposeNotification.Task.GetAwaiter().GetResult();
            }
        };

        var dispose = Task.Run(async () => await registry.DisposeAsync());
        try
        {
            await disposedNameRemoved.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
    public async Task RegistryNotificationsCanReenterClearAndDispose()
    {
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueStateRegistry<AppSettings, AppSettings.Fragment>(
            (_, name) =>
            {
                var store = new InMemoryStateSource<AppSettings.Fragment>();
                return new StateSourceSet<AppSettings.Fragment>([
                    new(name, store, writer: store, watcher: store),
                ]);
            }
        );
        await using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IConfiglueStateRegistry<AppSettings>>();
        (await registry.TryAddAsync("clear")).ShouldBeTrue();
        registry.StateRemoved += name =>
        {
            if (name == "clear")
            {
                _ = registry.ClearAsync();
            }
        };

        (
            await Task.Run(async () => await registry.TryRemoveAsync("clear")).WaitAsync(TimeSpan.FromSeconds(5))
        ).ShouldBeTrue();

        var listenerAfterFailureWasCalled = false;
        registry.StateAdded += (name, _) =>
        {
            if (name == "listener-error")
            {
                throw new InvalidOperationException("listener failure");
            }
        };
        registry.StateAdded += (name, _) =>
        {
            if (name == "listener-error")
            {
                listenerAfterFailureWasCalled = true;
            }
        };
        (await registry.TryAddAsync("listener-error")).ShouldBeTrue();
        (listenerAfterFailureWasCalled).ShouldBeTrue();
        (await registry.TryRemoveAsync("listener-error")).ShouldBeTrue();

        (await registry.TryAddAsync("dispose")).ShouldBeTrue();
        registry.StateRemoved += name =>
        {
            if (name == "dispose")
            {
                _ = registry.DisposeAsync();
            }
        };

        (
            await Task.Run(async () => await registry.TryRemoveAsync("dispose")).WaitAsync(TimeSpan.FromSeconds(5))
        ).ShouldBeTrue();
    }
}
