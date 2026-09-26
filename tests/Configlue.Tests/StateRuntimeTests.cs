using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

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
}
