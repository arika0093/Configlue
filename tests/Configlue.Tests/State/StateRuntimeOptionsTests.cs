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
    public async Task Options_ReturnsModelDefaultsWhenEverySourceIsMissing()
    {
        var missing = new InMemoryStateSource<AppSettings.Fragment>();
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("optional", missing, writer: missing),
        ]);
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(sourceSet);

        var result = await options.ReadAsync();
        var saved = await options.SaveAsync(patch => patch.RetryCount = 7);

        (result.Status).ShouldBe(StateReadStatus.Success);
        (result.Value!.Enabled).ShouldBeTrue();
        (result.Value.RetryCount).ShouldBe(3);
        (result.Value.Database!.Host).ShouldBe("localhost");
        (result.Value.Plugins).ShouldBeEmpty();
        (saved.Revision).ShouldBe("1");
    }

    [Test]
    public async Task Options_NotifiesSubscribersWhenAWatchedSourceChanges()
    {
        var user = new InMemoryStateSource<AppSettings.Fragment>();
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("user", user, priority: 100, watcher: user),
            new("defaults", defaults, priority: 0, watcher: defaults),
        ]);
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            sourceSet
        );
        var changedValue = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var resetValue = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
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

        (updated).ShouldBe(8);
        (reset).ShouldBe(3);
    }

    [Test]
    public async Task Options_ShadowedReloadNotifiesReloadListenersWithoutAChangeNotification()
    {
        var user = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
        );
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("user", user, priority: 100, watcher: user),
            new("defaults", defaults, priority: 0, watcher: defaults),
        ]);
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            sourceSet
        );
        var changeNotifications = new List<int>();
        using var changeSubscription = options.OnChange(value =>
        {
            lock (changeNotifications)
            {
                changeNotifications.Add(value.RetryCount);
            }
        });
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reloadSubscription = ((IConfiglueReloadDiagnostics)options).OnReload(_ =>
            reloaded.TrySetResult()
        );

        defaults.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) });
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (await options.GetValueAsync()).RetryCount.ShouldBe(8);
        lock (changeNotifications)
        {
            changeNotifications.ShouldBeEmpty();
        }
    }

    [Test]
    public async Task Options_EffectiveChangeNotifiesChangeListenerButNotReloadListener()
    {
        var user = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
        );
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("user", user, priority: 100, watcher: user),
            new("defaults", defaults, priority: 0, watcher: defaults),
        ]);
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            sourceSet
        );
        var changed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var changeSubscription = options.OnChange(value =>
            changed.TrySetResult(value.RetryCount)
        );
        var reloadCount = 0;
        using var reloadSubscription = ((IConfiglueReloadDiagnostics)options).OnReload(_ =>
            Interlocked.Increment(ref reloadCount)
        );

        user.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) });
        var updated = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (updated).ShouldBe(9);
        Volatile.Read(ref reloadCount).ShouldBe(0);
    }

    [Test]
    public async Task Options_CompositeReaderPreservesNestedIdentityAndPhysicalOrigin()
    {
        var primary = new OpaqueRevisionStateStore<AppSettings.Fragment>(
            StateReadStatus.Unavailable,
            physicalOrigin: "primary://settings"
        );
        var fallback = new OpaqueRevisionStateStore<AppSettings.Fragment>(
            StateReadStatus.Success,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) },
            "fallback://settings"
        );
        var primaryRuntime = new CompositeStateRuntime<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new(
                    "database",
                    primary,
                    writer: new NoOpSourceWriter<AppSettings.Fragment>(),
                    watcher: primary,
                    physicalOrigin: "primary://settings"
                ),
            ])
        );
        var runtime = new CompositeStateRuntime<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new(
                    "remote",
                    primaryRuntime.Reader,
                    priority: 100,
                    fallbackCondition: StateFallbackCondition.Unavailable,
                    watcher: primaryRuntime.Watcher,
                    physicalOrigin: "logical://remote"
                ),
                new(
                    "local",
                    fallback,
                    priority: 0,
                    writer: new NoOpSourceWriter<AppSettings.Fragment>(),
                    watcher: fallback,
                    physicalOrigin: "fallback://settings"
                ),
            ])
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new(
                    "composite",
                    runtime.Reader,
                    writer: runtime.Writer,
                    watcher: runtime.Watcher,
                    physicalOrigin: "logical://settings"
                ),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );

        var initial = await options.ReadAsync();
        var initialDetails = await options.GetDetailsAsync();
        using var staleSession = await options.OpenEditSessionAsync();
        staleSession.Value.RetryCount = 8;
        var changed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = options.OnChange(value => changed.TrySetResult(value.RetryCount));

        (initial.Value!.RetryCount).ShouldBe(4);
        (initial.Revision).ShouldBe("opaque");
        (initial.PhysicalOrigin).ShouldBe("fallback://settings");
        (
            initialDetails
                .RetryCount.Sources.Single(s =>
                    s.IsPresent && s.Source.Locator == "fallback://settings"
                )
                .Source.Locator
        ).ShouldBe("fallback://settings");
        initial.Revisions!.NestedRevisions.Count.ShouldBe(1);
        var initialNested = initial.Revisions.NestedRevisions[SourceId.From("composite")];
        initialNested.TryGetRevision(SourceId.From("remote"), out _).ShouldBeTrue();
        initialNested.TryGetRevision(SourceId.From("local"), out _).ShouldBeTrue();
        initialNested.NestedRevisions.Count.ShouldBe(1);
        initialNested.NestedRevisions[SourceId.From("remote")]
            .TryGetRevision(SourceId.From("database"), out _)
            .ShouldBeTrue();
        await primary.WatchStarted.WaitAsync(TimeSpan.FromSeconds(5));

        primary.SetSuccess(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) },
            "primary://settings"
        );

        (await changed.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(9);
        var conflictThrown = false;
        try
        {
            await staleSession.CommitAsync();
        }
        catch (StateConflictException)
        {
            conflictThrown = true;
        }

        conflictThrown.ShouldBeTrue();
        var recovered = await options.ReadAsync();
        var recoveredDetails = await options.GetDetailsAsync();

        (recovered.Revision).ShouldBe("opaque");
        (recovered.PhysicalOrigin).ShouldBe("primary://settings");
        (
            recoveredDetails
                .RetryCount.Sources.Single(s =>
                    s.IsPresent && s.Source.Locator == "primary://settings"
                )
                .Source.Locator
        ).ShouldBe("primary://settings");
        recovered.Revisions!.NestedRevisions.Count.ShouldBe(1);
        var recoveredNested = recovered.Revisions.NestedRevisions[SourceId.From("composite")];
        recoveredNested.TryGetRevision(SourceId.From("remote"), out _).ShouldBeTrue();
        recoveredNested.TryGetRevision(SourceId.From("local"), out _).ShouldBeFalse();
        recoveredNested.NestedRevisions.Count.ShouldBe(1);
        (
            recoveredNested.NestedRevisions[SourceId.From("remote")]
                .TryGetRevision(SourceId.From("database"), out _)
        ).ShouldBeTrue();
    }

    [Test]
    public async Task EditSession_SavesDraftAndRejectsAStaleRevision()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("user", store, writer: store),
        ]);
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(sourceSet);
        using var session = await options.OpenEditSessionAsync();
        session.Value.RetryCount = 6;
        var save = await session.CommitAsync();
        var saved = await store.ReadAsync();

        (session.IsCommitted).ShouldBeTrue();
        (save.Revision).ShouldBe("2");
        (saved.Value!.RetryCount.Value).ShouldBe(6);

        using var stale = await options.OpenEditSessionAsync();
        stale.Value.RetryCount = 7;
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) });
        StateConflictException? conflict = null;
        try
        {
            await stale.CommitAsync();
        }
        catch (StateConflictException exception)
        {
            conflict = exception;
        }

        conflict.ShouldNotBeNull();
        conflict.Message.ShouldContain("RetryCount");
        (stale.IsCommitted).ShouldBeFalse();
    }

    [Test]
    public async Task EditSession_RebasesAChangeAfterAnUnrelatedPathChanges()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)])
        );
        using var session = await options.OpenEditSessionAsync();
        session.Value.RetryCount = 6;
        store.Set(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
                Enabled = Optional<bool>.Present(true),
            }
        );

        await session.CommitAsync();

        var resolved = (await options.ReadAsync()).Value!;
        resolved.RetryCount.ShouldBe(6);
        resolved.Enabled.ShouldBeTrue();
    }

    [Test]
    public async Task EditSession_RebasesNestedDisjointChanges()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("before"),
                        Port = Optional<int>.Present(5432),
                    }
                ),
            }
        );
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)])
        );
        using var session = await options.OpenEditSessionAsync();
        session.Value.Database!.Host = "session-host";
        store.Set(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("before"),
                        Port = Optional<int>.Present(7443),
                    }
                ),
            }
        );

        await session.CommitAsync();

        var resolved = (await options.ReadAsync()).Value!;
        resolved.Database!.Host.ShouldBe("session-host");
        resolved.Database.Port.ShouldBe(7443);
    }

    [Test]
    public async Task SaveAsync_UpdatesACloneSynchronouslyOrAsynchronously()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
                Plugins = Optional<IReadOnlyList<string>>.Present(["existing"]),
            }
        );
        var sources = new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)]);
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(sources);

        using (var edit = await options.OpenEditSessionAsync())
        {
            edit.Value.RetryCount++;
            edit.Value.Plugins = [.. edit.Value.Plugins, "sync"];
            await edit.CommitAsync();
        }
        using (var edit = await options.OpenEditSessionAsync())
        {
            await Task.Yield();
            edit.Value.RetryCount++;
            edit.Value.Plugins = [.. edit.Value.Plugins, "async"];
            await edit.CommitAsync();
        }

        var saved = await store.ReadAsync();
        var resolved = await options.ReadAsync();
        (saved.Value!.RetryCount.Value).ShouldBe(5);
        ((saved.Value.Plugins.Value!))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "existing", "sync", "async" }).OrderBy(static item => item));
        (resolved.Value!.RetryCount).ShouldBe(5);
    }

    [Test]
    public async Task SaveAsync_DoesNotRetainCallerOwnedCollectionReferences()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>();
        var sources = new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)]);
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(sources);
        var plugins = new List<string> { "before-save" };

        await options.SaveAsync(patch => patch.Plugins = plugins);
        plugins.Add("after-save");

        var saved = await options.ReadAsync();
        saved.Value!.Plugins.ShouldBe(new[] { "before-save" });
    }

    [Test]
    public async Task EditSession_RebasesAfterAnUnrelatedSourceChanges()
    {
        var user = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Enabled = Optional<bool>.Present(true) }
        );
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults, priority: 0),
        ]);
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(sources);
        using var session = await options.OpenEditSessionAsync();
        session.Value.Enabled = false;
        defaults.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) });

        await session.CommitAsync();

        var storedUser = await user.ReadAsync();
        var resolved = await options.ReadAsync();
        (storedUser.Value!.Enabled.Value).ShouldBeFalse();
        (storedUser.Revision).ShouldBe("2");
        resolved.Value!.RetryCount.ShouldBe(8);
        resolved.Value.Enabled.ShouldBeFalse();
    }

    [Test]
    public async Task EditSession_WritesOnlySemanticChangesToTheSelectedContribution()
    {
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("defaults.db"),
                        Port = Optional<int>.Present(5432),
                    }
                ),
            }
        );
        var user = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Label = Optional<string?>.Present("user label"),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
                ),
            }
        );
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults),
        ]);
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(sources);

        using var session = await options.OpenEditSessionAsync();
        session.Value.RetryCount = 7;
        session.Value.Database!.Port = 7443;
        await session.CommitAsync();

        var storedUser = (await user.ReadAsync()).Value!;
        var resolved = (await options.ReadAsync()).Value!;
        (storedUser.RetryCount.IsPresent).ShouldBeTrue();
        (storedUser.RetryCount.Value).ShouldBe(7);
        (storedUser.Label.Value).ShouldBe("user label");
        (storedUser.Database.Value!.Host.IsPresent).ShouldBeFalse();
        (storedUser.Database.Value.Port.Value).ShouldBe(7443);
        (resolved.Database!.Host).ShouldBe("defaults.db");
        (resolved.Database.Port).ShouldBe(7443);
    }

    [Test]
    public async Task EditSession_RoutesNestedChangesToTheMostSpecificSources()
    {
        var user = new InMemoryStateSource<AppSettings.Fragment>(new AppSettings.Fragment());
        var database = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
                ),
            }
        );
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("defaults.db"),
                        Port = Optional<int>.Present(5432),
                    }
                ),
            }
        );
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", user, priority: 100, writer: user),
                new("database", database, priority: 50, writer: database),
                new("defaults", defaults),
            ]),
            StateWritePlan.DefaultTo(SourceId.From("user"))
        );
        var writePlan = new StateWritePlan(
            null,
            new Dictionary<string, SourceId>(StringComparer.Ordinal)
            {
                ["Database"] = SourceId.From("database"),
                ["Database.Port"] = SourceId.From("user"),
            }
        );

        using var session = await options.OpenEditSessionAsync(writePlan);
        session.Value.RetryCount = 7;
        session.Value.Database!.Host = "session.db";
        session.Value.Database.Port = 7443;
        var result = await session.CommitAsync();

        var userFragment = (await user.ReadAsync()).Value!;
        var databaseFragment = (await database.ReadAsync()).Value!;
        var resolved = (await options.ReadAsync()).Value!;
        (userFragment.RetryCount.Value).ShouldBe(7);
        (userFragment.Database.Value!.Host.IsPresent).ShouldBeFalse();
        (userFragment.Database.Value.Port.Value).ShouldBe(7443);
        (databaseFragment.Database.Value!.Host.Value).ShouldBe("session.db");
        (databaseFragment.Database.Value.Port.Value).ShouldBe(6432);
        (resolved.RetryCount).ShouldBe(7);
        (resolved.Database!.Host).ShouldBe("session.db");
        (resolved.Database.Port).ShouldBe(7443);
        (result).ShouldNotBeNull();
        (result.PhysicalWriteCount).ShouldBe(2);
        result.Revision.ShouldBeNull();
        ((result.Sources.Select(static source => source.SourceId.Value)))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "user", "database" }).OrderBy(static item => item));
    }

    [Test]
    public async Task EditSession_PathPlanRejectsEditsHiddenByAHigherPrioritySource()
    {
        var policy = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Enabled = Optional<bool>.Present(true) }
        );
        var user = new InMemoryStateSource<AppSettings.Fragment>(new AppSettings.Fragment());
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("policy", policy, priority: 100),
                new("user", user, priority: 0, writer: user),
            ])
        );
        var userBefore = await user.ReadAsync();
        var writePlan = new StateWritePlan(
            null,
            new Dictionary<string, SourceId>(StringComparer.Ordinal)
            {
                ["Enabled"] = SourceId.From("user"),
            }
        );

        using var session = await options.OpenEditSessionAsync(writePlan);
        session.Value.Enabled = false;
        StateConflictException? rejection = null;
        try
        {
            await session.CommitAsync();
        }
        catch (StateConflictException exception)
        {
            rejection = exception;
        }

        var userAfter = await user.ReadAsync();
        rejection.ShouldNotBeNull();
        rejection.Message.ShouldContain("Enabled");
        rejection.Message.ShouldContain("policy");
        (userAfter.Value!.Enabled.IsPresent).ShouldBeFalse();
        (userAfter.Revision).ShouldBe(userBefore.Revision);
    }

    [Test]
    public async Task EditSession_DoesNotWriteWhenTheModelWasNotChanged()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)])
        );

        using var session = await options.OpenEditSessionAsync();
        var result = await session.CommitAsync();

        (session.IsCommitted).ShouldBeTrue();
        result.Revision.ShouldBeNull();
        result.Sources.ShouldBeEmpty();
        result.PhysicalWriteCount.ShouldBe(0);
        ((await store.ReadAsync()).Revision).ShouldBe("1");
    }

    [Test]
    public async Task EditSession_RebasesAppendEditsOntoTheSelectedSourceSegment()
    {
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["base"]) }
        );
        var user = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["user"]) }
        );
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", user, priority: 100, writer: user),
                new("defaults", defaults),
            ])
        );

        using var session = await options.OpenEditSessionAsync();
        session.Value.Plugins = [.. session.Value.Plugins, "session"];
        await session.CommitAsync();

        var storedUser = (await user.ReadAsync()).Value!;
        var resolved = (await options.ReadAsync()).Value!;
        ((storedUser.Plugins.Value!))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "user", "session" }).OrderBy(static item => item));
        ((resolved.Plugins))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "base", "user", "session" }).OrderBy(static item => item));
    }

    [Test]
    public async Task EditSession_RebasesConcurrentAppendAdditions()
    {
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["base"]) }
        );
        var user = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["user"]) }
        );
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", user, priority: 100, writer: user),
                new("defaults", defaults),
            ])
        );
        using var session = await options.OpenEditSessionAsync();
        session.Value.Plugins = [.. session.Value.Plugins, "session"];
        user.Set(
            new AppSettings.Fragment
            {
                Plugins = Optional<IReadOnlyList<string>>.Present(["user", "external"]),
            }
        );

        await session.CommitAsync();

        var resolved = (await options.ReadAsync()).Value!;
        resolved
            .Plugins.OrderBy(static value => value)
            .ShouldBe(
                (new[] { "base", "user", "external", "session" }).OrderBy(static value => value)
            );
    }

    [Test]
    public async Task EditSession_RebasesSetUnionEditsAndRejectsRemovingOtherSourceValues()
    {
        var defaults = new InMemoryStateSource<SetUnionSettings.Fragment>(
            new SetUnionSettings.Fragment
            {
                Tags = Optional<IReadOnlyList<string>>.Present(["base", "shared"]),
            }
        );
        var user = new InMemoryStateSource<SetUnionSettings.Fragment>(
            new SetUnionSettings.Fragment
            {
                Tags = Optional<IReadOnlyList<string>>.Present(["user", "shared"]),
            }
        );
        var options = new ConfiglueRuntime<SetUnionSettings, SetUnionSettings.Fragment>(
            new StateSourceSet<SetUnionSettings.Fragment>([
                new("user", user, priority: 100, writer: user),
                new("defaults", defaults),
            ])
        );

        using var session = await options.OpenEditSessionAsync();
        session.Value.Tags = [.. session.Value.Tags, "session"];
        await session.CommitAsync();

        var storedUser = (await user.ReadAsync()).Value!;
        var resolved = (await options.ReadAsync()).Value!;
        ((storedUser.Tags.Value!))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "user", "session" }).OrderBy(static item => item));
        ((resolved.Tags))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "base", "shared", "user", "session" }).OrderBy(static item => item));

        using var removeLower = await options.OpenEditSessionAsync();
        removeLower.Value.Tags = removeLower
            .Value.Tags.Where(static tag => tag != "base")
            .ToArray();
        var rejected = false;
        try
        {
            await removeLower.CommitAsync();
        }
        catch (StateConflictException)
        {
            rejected = true;
        }

        (rejected).ShouldBeTrue();
        ((await user.ReadAsync()).Revision).ShouldBe("2");
    }

    [Test]
    public async Task EditSession_RebasesConcurrentSetUnionAdditions()
    {
        var defaults = new InMemoryStateSource<SetUnionSettings.Fragment>(
            new SetUnionSettings.Fragment
            {
                Tags = Optional<IReadOnlyList<string>>.Present(["base"]),
            }
        );
        var user = new InMemoryStateSource<SetUnionSettings.Fragment>(
            new SetUnionSettings.Fragment
            {
                Tags = Optional<IReadOnlyList<string>>.Present(["user"]),
            }
        );
        var options = new ConfiglueRuntime<SetUnionSettings, SetUnionSettings.Fragment>(
            new StateSourceSet<SetUnionSettings.Fragment>([
                new("user", user, priority: 100, writer: user),
                new("defaults", defaults),
            ])
        );
        using var session = await options.OpenEditSessionAsync();
        session.Value.Tags = [.. session.Value.Tags, "session"];
        user.Set(
            new SetUnionSettings.Fragment
            {
                Tags = Optional<IReadOnlyList<string>>.Present(["user", "external"]),
            }
        );

        await session.CommitAsync();

        var resolved = (await options.ReadAsync()).Value!;
        resolved
            .Tags.OrderBy(static value => value)
            .ShouldBe(
                (new[] { "base", "user", "external", "session" }).OrderBy(static value => value)
            );
    }

    [Test]
    public async Task EditSession_RejectsAWriteHiddenByHigherPriorityReadOnlySource()
    {
        var policy = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Enabled = Optional<bool>.Present(true) }
        );
        var user = new InMemoryStateSource<AppSettings.Fragment>();
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("policy", policy, priority: 100),
                new("user", user, priority: 0, writer: user),
            ])
        );

        using var session = await options.OpenEditSessionAsync();
        session.Value.Enabled = false;
        var rejected = false;
        try
        {
            await session.CommitAsync();
        }
        catch (StateConflictException)
        {
            rejected = true;
        }

        (rejected).ShouldBeTrue();
        ((await user.ReadAsync()).Status).ShouldBe(StateReadStatus.NotFound);
    }

    // Keeps read-oriented composite runtimes constructible now that StateSourceWriter
    // resolves its write target once at construction time. The writer is never invoked
    // by the nested-identity test; it only satisfies single-writable-root inference.
    private sealed class NoOpSourceWriter<T> : ISourceWriter<T>
    {
        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            _ = request;
            _ = cancellationToken;
            return ValueTaskCompat.FromResult(new StateWriteResult("noop"));
        }
    }
}
