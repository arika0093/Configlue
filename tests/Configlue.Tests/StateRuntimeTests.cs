using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Tests;

public sealed class StateRuntimeTests
{
    [Test]
    public async Task Resolver_FallsBackByPolicyAndWatchesHigherPrioritySourceForFailback()
    {
        var primary = new InMemoryStateStore<string>();
        primary.SetUnavailable();
        var fallback = new InMemoryStateStore<string>("local");
        var sources = new StateSourceSet<string>(
        [
            new StateSource<string>("remote", primary, priority: 100,
                fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable, writer: primary, watcher: primary),
            new StateSource<string>("local", fallback, priority: 0, writer: fallback, watcher: fallback),
        ]);
        var runtime = new CompositeStateRuntime<string>(sources, StateWriteRoute.To("local"));

        var resolved = await runtime.Reader.ReadAsync();
        await runtime.Writer.WriteAsync(new StateWriteRequest<string>("edited locally", resolved.Revision));
        var localAfterWrite = await fallback.ReadAsync();
        var failbackWait = runtime.Watcher.WaitForChangeAsync(localAfterWrite.Revision).AsTask();
        primary.Set("remote");
        await failbackWait;
        var recovered = await runtime.Reader.ReadAsync();

        await Assert.That(resolved.Value).IsEqualTo("local");
        await Assert.That(resolved.SourceId).IsEqualTo("local");
        await Assert.That(resolved.Revisions!.Revisions.Count).IsEqualTo(2);
        await Assert.That(localAfterWrite.Value).IsEqualTo("edited locally");
        await Assert.That(recovered.Value).IsEqualTo("remote");
        await Assert.That(recovered.SourceId).IsEqualTo("remote");
        await Assert.That(runtime.Reader.ActiveSource!.Id).IsEqualTo("remote");
    }

    [Test]
    public async Task DependencyInjection_ResolvesMergedOptionsAndSavesToConfiguredSource()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(4),
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Host = Optional<string>.Present("defaults.local"),
            }),
            Plugins = Optional<IReadOnlyList<string>>.Present(["base"]),
        });
        var user = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Port = Optional<int>.Present(6432),
            }),
            Plugins = Optional<IReadOnlyList<string>>.Present(["user"]),
        });
        var sources = new StateSourceSet<AppSettings.Fragment>(
        [
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults, priority: 0),
        ]);
        var services = new ServiceCollection();
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            _ => sources,
            StateWriteRoute.To("user"));
        using var serviceProvider = services.BuildServiceProvider();
        var readOnly = serviceProvider.GetRequiredService<IReadOnlyOptions<AppSettings>>();
        var writable = serviceProvider.GetRequiredService<IWritableOptions<AppSettings>>();

        await Assert.That(ReferenceEquals(readOnly, writable)).IsTrue();
        var resolved = await readOnly.ReadAsync();
        var currentValue = await readOnly.GetValueAsync();
        var saveResult = await writable.SaveAsync(new AppSettings
        {
            Enabled = true,
            RetryCount = 10,
            Label = "saved",
            Database = new DatabaseSettings { Host = "saved.local", Port = 7443 },
            Plugins = ["saved-plugin"],
        });
        var written = await user.ReadAsync();

        await Assert.That(resolved.Status).IsEqualTo(StateReadStatus.Success);
        await Assert.That(resolved.SourceId).IsEqualTo("user");
        await Assert.That(resolved.Revisions!.Revisions.Count).IsEqualTo(2);
        await Assert.That(resolved.Value!.Enabled).IsFalse();
        await Assert.That(currentValue.RetryCount).IsEqualTo(4);
        await Assert.That(resolved.Value.RetryCount).IsEqualTo(4);
        await Assert.That(resolved.Value.Database!.Host).IsEqualTo("defaults.local");
        await Assert.That(resolved.Value.Database.Port).IsEqualTo(6432);
        await Assert.That(resolved.Value.Plugins).IsEquivalentTo(["base", "user"]);
        await Assert.That(saveResult.Revision).IsEqualTo("2");
        await Assert.That(written.Value!.RetryCount.Value).IsEqualTo(10);
        await Assert.That(written.Value.Database!.Value!.Host.Value).IsEqualTo("saved.local");
        await Assert.That(written.Value.Plugins.Value).IsEquivalentTo(["saved-plugin"]);
    }

    [Test]
    public async Task Options_ReturnsModelDefaultsWhenEverySourceIsMissing()
    {
        var missing = new InMemoryStateStore<AppSettings.Fragment>();
        var sourceSet = new StateSourceSet<AppSettings.Fragment>(
        [
            new("optional", missing, writer: missing),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sourceSet);

        var result = await options.ReadAsync();
        var saved = await options.SaveAsync(new AppSettings { RetryCount = 7 });

        await Assert.That(result.Status).IsEqualTo(StateReadStatus.Success);
        await Assert.That(result.Value!.Enabled).IsTrue();
        await Assert.That(result.Value.RetryCount).IsEqualTo(3);
        await Assert.That(result.Value.Database!.Host).IsEqualTo("localhost");
        await Assert.That(result.Value.Plugins).IsEmpty();
        await Assert.That(saved.Revision).IsEqualTo("1");
    }

    [Test]
    public async Task Options_NotifiesSubscribersWhenAWatchedSourceChanges()
    {
        var user = new InMemoryStateStore<AppSettings.Fragment>();
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
        });
        var sourceSet = new StateSourceSet<AppSettings.Fragment>(
        [
            new("user", user, priority: 100, watcher: user),
            new("defaults", defaults, priority: 0, watcher: defaults),
        ]);
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sourceSet);
        var changedValue = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetValue = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = options.OnChange(value =>
        {
            if (value.RetryCount == 8)
            {
                changedValue.TrySetResult(value.RetryCount);
            }
            else if (value.RetryCount == 3)
            {
                resetValue.TrySetResult(value.RetryCount);
            }
        });

        defaults.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) });
        var updated = await changedValue.Task.WaitAsync(TimeSpan.FromSeconds(5));
        defaults.SetNotFound();
        var reset = await resetValue.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(updated).IsEqualTo(8);
        await Assert.That(reset).IsEqualTo(3);
    }

    [Test]
    public async Task ConfigureSession_SavesDraftAndRejectsAStaleRevision()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
        });
        var sourceSet = new StateSourceSet<AppSettings.Fragment>(
        [
            new("user", store, writer: store),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sourceSet);
        using var session = await options.BeginConfigureAsync();
        session.Value.RetryCount = 6;
        var save = await session.SaveAsync();
        var saved = await store.ReadAsync();

        await Assert.That(session.IsCommitted).IsTrue();
        await Assert.That(save.Revision).IsEqualTo("2");
        await Assert.That(saved.Value!.RetryCount.Value).IsEqualTo(6);

        using var stale = await options.BeginConfigureAsync();
        stale.Value.RetryCount = 7;
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) });
        var conflicted = false;
        try
        {
            await stale.SaveAsync();
        }
        catch (StateConflictException)
        {
            conflicted = true;
        }

        await Assert.That(conflicted).IsTrue();
        await Assert.That(stale.IsCommitted).IsFalse();
    }

    [Test]
    public async Task SaveAsync_UpdatesACloneSynchronouslyOrAsynchronously()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
            Plugins = Optional<IReadOnlyList<string>>.Present(["existing"]),
        });
        var sources = new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sources);

        await options.SaveAsync(settings =>
        {
            settings.RetryCount++;
            settings.Plugins = [.. settings.Plugins, "sync"];
        });
        await options.SaveAsync(async settings =>
        {
            await Task.Yield();
            settings.RetryCount++;
            settings.Plugins = [.. settings.Plugins, "async"];
        });

        var saved = await store.ReadAsync();
        var resolved = await options.ReadAsync();
        await Assert.That(saved.Value!.RetryCount.Value).IsEqualTo(5);
        await Assert.That(saved.Value.Plugins.Value).IsEquivalentTo(["existing", "sync", "async"]);
        await Assert.That(resolved.Value!.RetryCount).IsEqualTo(5);
    }

    [Test]
    public async Task Options_MigratesEachSourceFragmentBeforeMerging()
    {
        var oldSchema = new StateSchemaMetadata("app-settings", 1);
        var reader = new FixedStateReader<AppSettings.Fragment>(StateReadResult<AppSettings.Fragment>.Success(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) },
            "revision-1",
            oldSchema));
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([new("legacy", reader)]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sourceSet,
            migrations: [new AppSettingsV1ToV2Migration()]);

        var result = await options.ReadAsync();

        await Assert.That(result.Status).IsEqualTo(StateReadStatus.Success);
        await Assert.That(result.Value!.RetryCount).IsEqualTo(9);
        await Assert.That(result.Value.Label).IsEqualTo("migrated");
        await Assert.That(result.Schema).IsEqualTo(AppSettings.ConfiglueSchema.ToMetadata());
    }

    [Test]
    public async Task Options_ValidatesBeforeSavingAndLeavesTheStoredRevisionUntouched()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
        });
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)]);
        var services = new ServiceCollection();
        services.AddConfiglueValidator<AppSettings>(new RetryCountValidator());
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(sourceSet, validateDataAnnotations: true);
        using var serviceProvider = services.BuildServiceProvider();
        var writable = serviceProvider.GetRequiredService<IWritableOptions<AppSettings>>();
        OptionsValidationException? validationFailure = null;

        try
        {
            await writable.SaveAsync(new AppSettings { RetryCount = 101 });
        }
        catch (OptionsValidationException exception)
        {
            validationFailure = exception;
        }

        var stored = await store.ReadAsync();
        await Assert.That(validationFailure).IsNotNull();
        await Assert.That(validationFailure!.Failures.Count()).IsEqualTo(2);
        await Assert.That(validationFailure.Failures.Any(failure => failure.Contains("custom retry limit", StringComparison.Ordinal))).IsTrue();
        await Assert.That(stored.Value!.RetryCount.Value).IsEqualTo(3);
        await Assert.That(stored.Revision).IsEqualTo("1");

        using var edit = await writable.BeginConfigureAsync();
        edit.Value.RetryCount = 101;
        var editWasRejected = false;
        try
        {
            await edit.SaveAsync();
        }
        catch (OptionsValidationException)
        {
            editWasRejected = true;
        }

        edit.Value.RetryCount = 4;
        await edit.SaveAsync();
        var validStored = await store.ReadAsync();
        await Assert.That(editWasRejected).IsTrue();
        await Assert.That(edit.IsCommitted).IsTrue();
        await Assert.That(validStored.Value!.RetryCount.Value).IsEqualTo(4);
    }

    [Test]
    public async Task ApplyPatchAsync_ChangesOnlyTheTargetContributionAndUnsetRevealsLowerValues()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
            Label = Optional<string?>.Present("default label"),
        });
        var user = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            RetryCount = Optional<int>.Present(9),
            Label = Optional<string?>.Present("old label"),
        });
        var sourceSet = new StateSourceSet<AppSettings.Fragment>(
        [
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults, priority: 0),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sourceSet);
        var patch = new AppSettings.Patch
        {
            RetryCount = FragmentOperation<int>.Unset,
            Label = FragmentOperation<string?>.Set("patched label"),
        };

        var write = await options.ApplyPatchAsync(patch);
        var stored = await user.ReadAsync();
        var resolved = await options.ReadAsync();

        await Assert.That(write.Revision).IsEqualTo("2");
        await Assert.That(stored.Value!.Enabled.IsPresent).IsTrue();
        await Assert.That(stored.Value.RetryCount.IsPresent).IsFalse();
        await Assert.That(stored.Value.Label.Value).IsEqualTo("patched label");
        await Assert.That(resolved.Value!.Enabled).IsFalse();
        await Assert.That(resolved.Value.RetryCount).IsEqualTo(3);
        await Assert.That(resolved.Value.Label).IsEqualTo("patched label");
    }

    [Test]
    public async Task DependencyInjection_ResolvesNamedProfilesByServiceKey()
    {
        var primaryStore = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(5),
        });
        var secondaryStore = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(8),
        });
        var primarySources = new StateSourceSet<AppSettings.Fragment>([new("profile", primaryStore)]);
        var secondarySources = new StateSourceSet<AppSettings.Fragment>([new("profile", secondaryStore)]);
        var services = new ServiceCollection();
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>("primary", primarySources);
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>("secondary", secondarySources);
        using var serviceProvider = services.BuildServiceProvider();
        var primary = serviceProvider.GetRequiredKeyedService<IReadOnlyOptions<AppSettings>>("primary");
        var secondary = serviceProvider.GetRequiredKeyedService<IReadOnlyOptions<AppSettings>>("secondary");

        var primaryValue = await primary.GetValueAsync();
        var secondaryValue = await secondary.GetValueAsync();

        await Assert.That(primaryValue.RetryCount).IsEqualTo(5);
        await Assert.That(secondaryValue.RetryCount).IsEqualTo(8);
    }

    [Test]
    public async Task DependencyInjection_ManagesDynamicProfilesThroughRegistry()
    {
        var services = new ServiceCollection();
        var factoryCalls = 0;
        services.AddConfiglueOptionsRegistry<AppSettings, AppSettings.Fragment>((_, profileName) =>
        {
            Interlocked.Increment(ref factoryCalls);
            var store = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(profileName == "primary" ? 5 : 8),
            });
            return new StateSourceSet<AppSettings.Fragment>([new("profile", store)]);
        });
        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IConfiglueOptionsRegistry<AppSettings>>();
        var added = new List<string>();
        var removed = new List<string>();
        registry.ProfileAdded += (name, _) => added.Add(name);
        registry.ProfileRemoved += name => removed.Add(name);

        var addResults = new bool[16];
        Parallel.For(0, addResults.Length, index => addResults[index] = registry.TryAdd("primary"));
        registry.TryAdd("secondary");
        var primary = await registry.Get("primary").GetValueAsync();
        var secondary = await registry.Get("secondary").GetValueAsync();
        var removedPrimary = registry.TryRemove("primary");
        var removedAgain = registry.TryRemove("primary");

        await Assert.That(addResults.Count(static result => result)).IsEqualTo(1);
        await Assert.That(factoryCalls).IsEqualTo(2);
        await Assert.That(primary.RetryCount).IsEqualTo(5);
        await Assert.That(secondary.RetryCount).IsEqualTo(8);
        await Assert.That(registry.ProfileNames).IsEquivalentTo(["secondary"]);
        await Assert.That(added).IsEquivalentTo(["primary", "secondary"]);
        await Assert.That(removed).IsEquivalentTo(["primary"]);
        await Assert.That(removedPrimary).IsTrue();
        await Assert.That(removedAgain).IsFalse();
    }

    private sealed class FixedStateReader<T>(StateReadResult<T> result) : IStateReader<T>
    {
        public ValueTask<StateReadResult<T>> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AppSettingsV1ToV2Migration : IStateSchemaMigration<AppSettings.Fragment>
    {
        public StateSchemaMetadata SourceSchema => new("app-settings", 1);

        public StateSchemaMetadata TargetSchema => new("app-settings", 2);

        public ValueTask<AppSettings.Fragment> MigrateAsync(
            AppSettings.Fragment value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var builder = value.ToBuilder();
            builder.Label = Optional<string?>.Present("migrated");
            return ValueTask.FromResult(builder.Build());
        }
    }

    private sealed class RetryCountValidator : IValidateOptions<AppSettings>
    {
        public ValidateOptionsResult Validate(string? name, AppSettings options) =>
            options.RetryCount > 10
                ? ValidateOptionsResult.Fail("RetryCount exceeds the custom retry limit.")
                : ValidateOptionsResult.Success;
    }
}
