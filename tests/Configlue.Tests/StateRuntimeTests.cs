using Configlue.Testing;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
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
    public async Task DependencyInjection_ProvidesMicrosoftOptionsAdapters()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(17),
        });
        var services = new ServiceCollection();
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("default", store, writer: store)]));
        using var serviceProvider = services.BuildServiceProvider();

        var options = serviceProvider.GetRequiredService<IOptions<AppSettings>>();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        using var scope = serviceProvider.CreateScope();
        var snapshot = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<AppSettings>>();
        var firstSnapshot = snapshot.Value;
        var secondSnapshot = snapshot.Get(Options.DefaultName);

        await Assert.That(options.Value.RetryCount).IsEqualTo(17);
        await Assert.That(monitor.CurrentValue.RetryCount).IsEqualTo(17);
        await Assert.That(ReferenceEquals(firstSnapshot, secondSnapshot)).IsTrue();
        await Assert.That(firstSnapshot.RetryCount).IsEqualTo(17);
    }

    [Test]
    public async Task OptionsMonitor_ResolvesNamedProfilesAndPublishesTheirChanges()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
        });
        var custom = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(8),
        });
        var services = new ServiceCollection();
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("default", defaults, writer: defaults)]),
            onChangeDebounce: TimeSpan.Zero);
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            "custom",
            new StateSourceSet<AppSettings.Fragment>([new("custom", custom, writer: custom, watcher: custom)]),
            onChangeDebounce: TimeSpan.Zero);
        using var serviceProvider = services.BuildServiceProvider();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        var changed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var directChanged = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = monitor.OnChange((value, name) =>
        {
            if (name == "custom")
            {
                changed.TrySetResult(value.RetryCount);
            }
        });
        using var directSubscription = serviceProvider.GetRequiredKeyedService<IReadOnlyOptions<AppSettings>>("custom")
            .OnChange(value => directChanged.TrySetResult(value.RetryCount));

        await Assert.That(monitor.Get("custom").RetryCount).IsEqualTo(8);
        custom.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) });
        await Assert.That(await directChanged.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(12);
        await Assert.That(await changed.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(12);
    }

    [Test]
    public async Task OptionsMonitor_FollowsProfilesAddedToRuntimeRegistry()
    {
        var stores = new Dictionary<string, InMemoryStateStore<AppSettings.Fragment>>(StringComparer.Ordinal);
        var services = new ServiceCollection();
        services.AddConfiglueOptionsRegistry<AppSettings, AppSettings.Fragment>((_, profileName) =>
        {
            var store = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(4),
            });
            stores.Add(profileName, store);
            return new StateSourceSet<AppSettings.Fragment>(
            [new(profileName, store, writer: store, watcher: store)]);
        }, onChangeDebounce: TimeSpan.Zero);
        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IConfiglueOptionsRegistry<AppSettings>>();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        var changed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = monitor.OnChange((value, name) =>
        {
            if (name == "runtime")
            {
                changed.TrySetResult(value.RetryCount);
            }
        });

        await Assert.That(registry.TryAdd("runtime")).IsTrue();
        await Assert.That(monitor.Get("runtime").RetryCount).IsEqualTo(4);
        stores["runtime"].Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(14) });
        await Assert.That(await changed.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(14);
    }

    [Test]
    public async Task ApplyPatchesAsync_GroupsSiblingJsonSectionsIntoOnePhysicalWrite()
    {
        var resource = new InMemoryResource();
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var firstSection = new JsonSectionResource(resource, "App:First");
        var secondSection = new JsonSectionResource(resource, "App:Second");
        var firstSource = SerializedStateSource.FromResource<AppSettings.Fragment>("first", firstSection, codec, priority: 10);
        var secondSource = SerializedStateSource.FromResource<AppSettings.Fragment>("second", secondSection, codec, priority: 0);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [firstSource, secondSource]));

        var result = await options.ApplyPatchesAsync(
        [
            new StateSourcePatch("first", new AppSettings.Patch
            {
                RetryCount = FragmentOperation<int>.Set(7),
            }),
            new StateSourcePatch("second", new AppSettings.Patch
            {
                Label = FragmentOperation<string?>.Set("second section"),
            }),
        ]);
        var first = await firstSource.Reader.ReadAsync();
        var second = await secondSource.Reader.ReadAsync();
        var stored = await resource.ReadAsync();

        await Assert.That(result.Sources.Count).IsEqualTo(2);
        await Assert.That(result.PhysicalWriteCount).IsEqualTo(1);
        await Assert.That(resource.WriteCount).IsEqualTo(1);
        await Assert.That(result.Sources.Select(static item => item.Revision).Distinct().Count()).IsEqualTo(1);
        await Assert.That(first.Value!.RetryCount.Value).IsEqualTo(7);
        await Assert.That(second.Value!.Label.Value).IsEqualTo("second section");
        await Assert.That(stored.Status).IsEqualTo(StateReadStatus.Success);
    }

    [Test]
    public async Task ApplyPatchesAsync_PersistsGroupedSectionsThroughFileResource()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"configlue-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var resource = new FileResource(Path.Combine(directory, "settings.json"));
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var first = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "first",
            new JsonSectionResource(resource, "App:First"),
            codec);
        var second = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "second",
            new JsonSectionResource(resource, "App:Second"),
            codec);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>([first, second]));

        try
        {
            var result = await options.ApplyPatchesAsync(
            [
                new StateSourcePatch("first", new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(5) }),
                new StateSourcePatch("second", new AppSettings.Patch { Label = FragmentOperation<string?>.Set("file batch") }),
            ]);
            var firstState = await first.Reader.ReadAsync();
            var secondState = await second.Reader.ReadAsync();

            await Assert.That(result.PhysicalWriteCount).IsEqualTo(1);
            await Assert.That(firstState.Value!.RetryCount.Value).IsEqualTo(5);
            await Assert.That(secondState.Value!.Label.Value).IsEqualTo("file batch");
            await Assert.That(firstState.Revision).IsEqualTo(secondState.Revision);
        }
        finally
        {
            resource.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task ApplyPatchesAsync_RejectsOverlappingResourceScopesBeforeWriting()
    {
        var resource = new InMemoryResource();
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var parent = new JsonSectionResource(resource, "App");
        var child = new JsonSectionResource(resource, "App:Child");
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [
            SerializedStateSource.FromResource<AppSettings.Fragment>("parent", parent, codec, priority: 10),
            SerializedStateSource.FromResource<AppSettings.Fragment>("child", child, codec, priority: 0),
        ]));
        var failed = false;
        try
        {
            await options.ApplyPatchesAsync(
            [
                new StateSourcePatch("parent", new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }),
                new StateSourcePatch("child", new AppSettings.Patch { Label = FragmentOperation<string?>.Set("child") }),
            ]);
        }
        catch (StateConflictException)
        {
            failed = true;
        }

        await Assert.That(failed).IsTrue();
        await Assert.That(resource.WriteCount).IsEqualTo(0);

        var json = new JsonSectionResource(resource, "App:Json");
        var xml = new XmlSectionResource(resource, "App:Xml");
        var differentDomains = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [
            SerializedStateSource.FromResource<AppSettings.Fragment>("json", json, codec),
            SerializedStateSource.FromResource<AppSettings.Fragment>("xml", xml, new XmlStateCodec<AppSettings.Fragment>()),
        ]));
        var domainConflict = false;
        try
        {
            await differentDomains.ApplyPatchesAsync(
            [
                new StateSourcePatch("json", new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(3) }),
                new StateSourcePatch("xml", new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(4) }),
            ]);
        }
        catch (NotSupportedException)
        {
            domainConflict = true;
        }

        await Assert.That(domainConflict).IsTrue();
        await Assert.That(resource.WriteCount).IsEqualTo(0);
    }

    [Test]
    public async Task ApplyPatchesAsync_BatchesXmlAndYamlSectionUpdates()
    {
        var xmlResource = new InMemoryResource();
        var xmlCodec = new XmlStateCodec<AppSettings.Fragment>();
        var xmlFirst = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "xml-first",
            new XmlSectionResource(xmlResource, "App:First"),
            xmlCodec);
        var xmlSecond = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "xml-second",
            new XmlSectionResource(xmlResource, "App:Second"),
            xmlCodec);
        var xmlOptions = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [xmlFirst, xmlSecond]));
        var xmlResult = await xmlOptions.ApplyPatchesAsync(
        [
            new StateSourcePatch("xml-first", new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(6) }),
            new StateSourcePatch("xml-second", new AppSettings.Patch { Label = FragmentOperation<string?>.Set("xml") }),
        ]);

        var yamlResource = new InMemoryResource();
        var yamlCodec = new YamlStateCodec<AppSettings.Fragment>();
        var yamlFirst = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "yaml-first",
            new YamlSectionResource(yamlResource, "App:First"),
            yamlCodec);
        var yamlSecond = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "yaml-second",
            new YamlSectionResource(yamlResource, "App:Second"),
            yamlCodec);
        var yamlOptions = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [yamlFirst, yamlSecond]));
        var yamlResult = await yamlOptions.ApplyPatchesAsync(
        [
            new StateSourcePatch("yaml-first", new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(8) }),
            new StateSourcePatch("yaml-second", new AppSettings.Patch { Label = FragmentOperation<string?>.Set("yaml") }),
        ]);

        await Assert.That(xmlResult.PhysicalWriteCount).IsEqualTo(1);
        await Assert.That(xmlResource.WriteCount).IsEqualTo(1);
        await Assert.That((await xmlFirst.Reader.ReadAsync()).Value!.RetryCount.Value).IsEqualTo(6);
        await Assert.That((await xmlSecond.Reader.ReadAsync()).Value!.Label.Value).IsEqualTo("xml");
        await Assert.That(yamlResult.PhysicalWriteCount).IsEqualTo(1);
        await Assert.That(yamlResource.WriteCount).IsEqualTo(1);
        await Assert.That((await yamlFirst.Reader.ReadAsync()).Value!.RetryCount.Value).IsEqualTo(8);
        await Assert.That((await yamlSecond.Reader.ReadAsync()).Value!.Label.Value).IsEqualTo("yaml");
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
    public async Task Options_DebouncesRapidSourceChangesAndReportsTheLatestValue()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
        });
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([new("user", store, watcher: store)]);
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sourceSet,
            onChangeDebounce: TimeSpan.FromMilliseconds(150));
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var latest = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = options.OnChange(value =>
        {
            notifications.Enqueue(value.RetryCount);
            latest.TrySetResult(value.RetryCount);
        });

        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) });
        await Task.Delay(TimeSpan.FromMilliseconds(30));
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) });
        var notifiedValue = await latest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        await Assert.That(notifiedValue).IsEqualTo(5);
        await Assert.That(notifications.ToArray()).IsEquivalentTo([5]);
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
    public async Task ConfigureSession_RejectsChangesToAnyParticipatingSource()
    {
        var user = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(true),
        });
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
        });
        var sources = new StateSourceSet<AppSettings.Fragment>(
        [
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults, priority: 0),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sources);
        using var session = await options.BeginConfigureAsync();
        session.Value.Enabled = false;
        defaults.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) });

        var conflicted = false;
        try
        {
            await session.SaveAsync();
        }
        catch (StateConflictException)
        {
            conflicted = true;
        }

        var storedUser = await user.ReadAsync();
        await Assert.That(conflicted).IsTrue();
        await Assert.That(storedUser.Value!.Enabled.Value).IsTrue();
        await Assert.That(storedUser.Revision).IsEqualTo("1");
    }

    [Test]
    public async Task ConfigureSession_WritesOnlySemanticChangesToTheSelectedContribution()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Host = Optional<string>.Present("defaults.db"),
                Port = Optional<int>.Present(5432),
            }),
        });
        var user = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Label = Optional<string?>.Present("user label"),
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Port = Optional<int>.Present(6432),
            }),
        });
        var sources = new StateSourceSet<AppSettings.Fragment>(
        [
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sources);

        using var session = await options.BeginConfigureAsync();
        session.Value.RetryCount = 7;
        session.Value.Database!.Port = 7443;
        await session.SaveAsync();

        var storedUser = (await user.ReadAsync()).Value!;
        var resolved = (await options.ReadAsync()).Value!;
        await Assert.That(storedUser.RetryCount.IsPresent).IsTrue();
        await Assert.That(storedUser.RetryCount.Value).IsEqualTo(7);
        await Assert.That(storedUser.Label.Value).IsEqualTo("user label");
        await Assert.That(storedUser.Database.Value!.Host.IsPresent).IsFalse();
        await Assert.That(storedUser.Database.Value.Port.Value).IsEqualTo(7443);
        await Assert.That(resolved.Database!.Host).IsEqualTo("defaults.db");
        await Assert.That(resolved.Database.Port).IsEqualTo(7443);
    }

    [Test]
    public async Task ConfigureSession_RoutesNestedChangesToTheMostSpecificSources()
    {
        var user = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment());
        var database = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Port = Optional<int>.Present(6432),
            }),
        });
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Host = Optional<string>.Present("defaults.db"),
                Port = Optional<int>.Present(5432),
            }),
        });
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [
            new("user", user, priority: 100, writer: user),
            new("database", database, priority: 50, writer: database),
            new("defaults", defaults),
        ]));
        var writePlan = new StateWritePlan(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Database"] = "database",
            ["Database.Port"] = "user",
        });

        using var session = await options.BeginConfigureAsync(writePlan);
        session.Value.RetryCount = 7;
        session.Value.Database!.Host = "session.db";
        session.Value.Database.Port = 7443;
        var result = await session.SaveAsync();

        var userFragment = (await user.ReadAsync()).Value!;
        var databaseFragment = (await database.ReadAsync()).Value!;
        var resolved = (await options.ReadAsync()).Value!;
        await Assert.That(userFragment.RetryCount.Value).IsEqualTo(7);
        await Assert.That(userFragment.Database.Value!.Host.IsPresent).IsFalse();
        await Assert.That(userFragment.Database.Value.Port.Value).IsEqualTo(7443);
        await Assert.That(databaseFragment.Database.Value!.Host.Value).IsEqualTo("session.db");
        await Assert.That(databaseFragment.Database.Value.Port.Value).IsEqualTo(6432);
        await Assert.That(resolved.RetryCount).IsEqualTo(7);
        await Assert.That(resolved.Database!.Host).IsEqualTo("session.db");
        await Assert.That(resolved.Database.Port).IsEqualTo(7443);
        await Assert.That(result.MultiWriteResult).IsNotNull();
        await Assert.That(result.MultiWriteResult!.PhysicalWriteCount).IsEqualTo(2);
        await Assert.That(result.MultiWriteResult.Sources.Select(static source => source.SourceId))
            .IsEquivalentTo(["user", "database"]);
    }

    [Test]
    public async Task ConfigureSession_PathPlanRejectsEditsHiddenByAHigherPrioritySource()
    {
        var policy = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(true),
        });
        var user = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment());
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [
            new("policy", policy, priority: 100),
            new("user", user, priority: 0, writer: user),
        ]));
        var userBefore = await user.ReadAsync();
        var writePlan = new StateWritePlan(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Enabled"] = "user",
        });

        using var session = await options.BeginConfigureAsync(writePlan);
        session.Value.Enabled = false;
        var rejected = false;
        try
        {
            await session.SaveAsync();
        }
        catch (StateConflictException)
        {
            rejected = true;
        }

        var userAfter = await user.ReadAsync();
        await Assert.That(rejected).IsTrue();
        await Assert.That(userAfter.Value!.Enabled.IsPresent).IsFalse();
        await Assert.That(userAfter.Revision).IsEqualTo(userBefore.Revision);
    }

    [Test]
    public async Task ConfigureSession_DoesNotWriteWhenTheModelWasNotChanged()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
        });
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)]));

        using var session = await options.BeginConfigureAsync();
        var result = await session.SaveAsync();

        await Assert.That(session.IsCommitted).IsTrue();
        await Assert.That(result.Revision).IsEqualTo("1");
        await Assert.That((await store.ReadAsync()).Revision).IsEqualTo("1");
    }

    [Test]
    public async Task ConfigureSession_RebasesAppendEditsOntoTheSelectedSourceSegment()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["base"]),
        });
        var user = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["user"]),
        });
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults),
        ]));

        using var session = await options.BeginConfigureAsync();
        session.Value.Plugins = [.. session.Value.Plugins, "session"];
        await session.SaveAsync();

        var storedUser = (await user.ReadAsync()).Value!;
        var resolved = (await options.ReadAsync()).Value!;
        await Assert.That(storedUser.Plugins.Value).IsEquivalentTo(["user", "session"]);
        await Assert.That(resolved.Plugins).IsEquivalentTo(["base", "user", "session"]);
    }

    [Test]
    public async Task ConfigureSession_RebasesSetUnionEditsAndRejectsRemovingOtherSourceValues()
    {
        var defaults = new InMemoryStateStore<SetUnionSettings.Fragment>(new SetUnionSettings.Fragment
        {
            Tags = Optional<IReadOnlyList<string>>.Present(["base", "shared"]),
        });
        var user = new InMemoryStateStore<SetUnionSettings.Fragment>(new SetUnionSettings.Fragment
        {
            Tags = Optional<IReadOnlyList<string>>.Present(["user", "shared"]),
        });
        var options = new ConfiglueOptions<SetUnionSettings, SetUnionSettings.Fragment>(new StateSourceSet<SetUnionSettings.Fragment>(
        [
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults),
        ]));

        using var session = await options.BeginConfigureAsync();
        session.Value.Tags = [.. session.Value.Tags, "session"];
        await session.SaveAsync();

        var storedUser = (await user.ReadAsync()).Value!;
        var resolved = (await options.ReadAsync()).Value!;
        await Assert.That(storedUser.Tags.Value).IsEquivalentTo(["user", "session"]);
        await Assert.That(resolved.Tags).IsEquivalentTo(["base", "shared", "user", "session"]);

        using var removeLower = await options.BeginConfigureAsync();
        removeLower.Value.Tags = removeLower.Value.Tags.Where(static tag => tag != "base").ToArray();
        var rejected = false;
        try
        {
            await removeLower.SaveAsync();
        }
        catch (StateConflictException)
        {
            rejected = true;
        }

        await Assert.That(rejected).IsTrue();
        await Assert.That((await user.ReadAsync()).Revision).IsEqualTo("2");
    }

    [Test]
    public async Task ConfigureSession_RejectsAWriteHiddenByHigherPriorityReadOnlySource()
    {
        var policy = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(true),
        });
        var user = new InMemoryStateStore<AppSettings.Fragment>();
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [
            new("policy", policy, priority: 100),
            new("user", user, priority: 0, writer: user),
        ]));

        using var session = await options.BeginConfigureAsync();
        session.Value.Enabled = false;
        var rejected = false;
        try
        {
            await session.SaveAsync();
        }
        catch (StateConflictException)
        {
            rejected = true;
        }

        await Assert.That(rejected).IsTrue();
        await Assert.That((await user.ReadAsync()).Status).IsEqualTo(StateReadStatus.NotFound);
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

    [Test]
    public async Task SourceProjection_MapsNestedSourceContractsAndRoutesWritesBack()
    {
        var remoteDatabase = new InMemoryStateStore<DatabaseSettings.Fragment>(new DatabaseSettings.Fragment
        {
            Host = Optional<string>.Present("remote.db"),
        });
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Host = Optional<string>.Present("default.db"),
                Port = Optional<int>.Present(5432),
            }),
        });
        var databaseSource = new StateSource<DatabaseSettings.Fragment>(
            "remote-database", remoteDatabase, priority: 100, writer: remoteDatabase, physicalOrigin: "database-row");
        var projectedSource = StateSourceProjection.Project<DatabaseSettings.Fragment, AppSettings.Fragment>(
            databaseSource,
            fragment => new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(fragment),
            },
            root => root.Database.IsPresent ? root.Database.Value! : DatabaseSettings.Fragment.Empty,
            AppSettings.ConfiglueSchema.ToMetadata());
        var sourceSet = new StateSourceSet<AppSettings.Fragment>(
        [
            projectedSource,
            new("defaults", defaults, priority: 0),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sourceSet,
            StateWriteRoute.To("remote-database"));

        var resolved = await options.ReadAsync();
        await options.ApplyPatchAsync(new AppSettings.Patch
        {
            Database = FragmentOperation<DatabaseSettings.Fragment?>.Set(new DatabaseSettings.Fragment
            {
                Host = Optional<string>.Present("saved.db"),
                Port = Optional<int>.Present(7443),
            }),
        });
        var savedSourceValue = await remoteDatabase.ReadAsync();
        var resolvedAfterWrite = await options.ReadAsync();

        await Assert.That(resolved.Value!.RetryCount).IsEqualTo(3);
        await Assert.That(resolved.Value.Database!.Host).IsEqualTo("remote.db");
        await Assert.That(resolved.Value.Database.Port).IsEqualTo(5432);
        await Assert.That(resolved.PhysicalOrigin).IsEqualTo("database-row");
        await Assert.That(savedSourceValue.Value!.Host.Value).IsEqualTo("saved.db");
        await Assert.That(savedSourceValue.Value.Port.Value).IsEqualTo(7443);
        await Assert.That(savedSourceValue.Value.Host.IsPresent).IsTrue();
        await Assert.That(resolvedAfterWrite.Value!.RetryCount).IsEqualTo(3);
    }

    [Test]
    public async Task SourceProjection_MigratesItsContractBeforeProjectingIntoTheRootModel()
    {
        var sourceSchema = new StateSchemaMetadata("database-settings", 1);
        var legacy = new FixedStateReader<DatabaseSettings.Fragment>(StateReadResult<DatabaseSettings.Fragment>.Success(
            new DatabaseSettings.Fragment { Host = Optional<string>.Present("legacy.db") },
            "legacy-database-revision",
            sourceSchema));
        var source = new StateSource<DatabaseSettings.Fragment>("legacy-database", legacy, priority: 100);
        var projected = StateSourceProjection.Project<DatabaseSettings.Fragment, AppSettings.Fragment>(
            source,
            fragment => new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(fragment),
            },
            projectedSchema: AppSettings.ConfiglueSchema.ToMetadata(),
            sourceMigrations: [new DatabaseV1ToV2Migration()],
            sourceSchema: DatabaseSettings.ConfiglueSchema.ToMetadata());
        var sources = new StateSourceSet<AppSettings.Fragment>([projected]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sources);

        var resolved = await options.ReadAsync();

        await Assert.That(resolved.Value!.Database!.Host).IsEqualTo("legacy.db");
        await Assert.That(resolved.Value.Database.Port).IsEqualTo(7400);
        await Assert.That(resolved.Schema).IsEqualTo(AppSettings.ConfiglueSchema.ToMetadata());
    }

    [Test]
    public async Task Options_ExplainsEffectiveNestedValuesAndSparseSourceContributions()
    {
        var user = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Port = Optional<int>.Present(6432),
            }),
        });
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Host = Optional<string>.Present("default.db"),
                Port = Optional<int>.Present(5432),
            }),
        });
        var sourceSet = new StateSourceSet<AppSettings.Fragment>(
        [
            new("user", user, priority: 100, physicalOrigin: "user-settings.json"),
            new("defaults", defaults, priority: 0, physicalOrigin: "defaults.json"),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sourceSet);

        var port = await options.ExplainAsync("Database.Port");
        var host = await options.ExplainAsync("Database.Host");
        var enabled = await options.ExplainAsync("Enabled");

        await Assert.That(port.EffectiveValue).IsEqualTo(6432);
        await Assert.That(port.HighestPrioritySourceId).IsEqualTo("user");
        await Assert.That(port.Contributions.Select(item => item.SourceId)).IsEquivalentTo(["user", "defaults"]);
        await Assert.That(port.Contributions[0].Value).IsEqualTo(6432);
        await Assert.That(port.Contributions[0].PhysicalOrigin).IsEqualTo("user-settings.json");
        await Assert.That(host.EffectiveValue).IsEqualTo("default.db");
        await Assert.That(host.HighestPrioritySourceId).IsEqualTo("defaults");
        await Assert.That((bool)enabled.EffectiveValue!).IsTrue();
        await Assert.That(enabled.Contributions).IsEmpty();
    }

    [Test]
    public async Task MigrateSourceAsync_CopiesOnlyTheSelectedContributionAfterSchemaMigration()
    {
        var legacySchema = new StateSchemaMetadata("app-settings", 1);
        var environment = new FixedStateReader<AppSettings.Fragment>(StateReadResult<AppSettings.Fragment>.Success(
            new AppSettings.Fragment { Label = Optional<string?>.Present("environment-value") },
            "environment-revision",
            AppSettings.ConfiglueSchema.ToMetadata()));
        var legacy = new FixedStateReader<AppSettings.Fragment>(StateReadResult<AppSettings.Fragment>.Success(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) },
            "legacy-revision",
            legacySchema));
        var target = new InMemoryStateStore<AppSettings.Fragment>();
        var sources = new StateSourceSet<AppSettings.Fragment>(
        [
            new("environment", environment, priority: 100),
            new("legacy", legacy, priority: 50),
            new("current", target, priority: 0, writer: target),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sources,
            migrations: [new AppSettingsV1ToV2Migration()]);

        var migration = await options.MigrateSourceAsync("legacy", "current");
        var copied = await target.ReadAsync();

        await Assert.That(migration.SourceId).IsEqualTo("legacy");
        await Assert.That(migration.TargetId).IsEqualTo("current");
        await Assert.That(migration.SourceRevision).IsEqualTo("legacy-revision");
        await Assert.That(migration.TargetRevision).IsEqualTo("1");
        await Assert.That(copied.Value!.RetryCount.Value).IsEqualTo(9);
        await Assert.That(copied.Value.Label.Value).IsEqualTo("migrated");
        await Assert.That(copied.Value.Label.Value).IsNotEqualTo("environment-value");
    }

    [Test]
    public async Task MigrateSourcesToTargetsAsync_MergesSelectedSourcesAndSkipsCompletedTargetsOnRetry()
    {
        var environment = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Label = Optional<string?>.Present("environment-value"),
        });
        var user = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Port = Optional<int>.Present(6432),
            }),
            Plugins = Optional<IReadOnlyList<string>>.Present(["user-plugin"]),
        });
        var legacy = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(12),
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Host = Optional<string>.Present("legacy.db"),
            }),
        });
        var primaryTarget = new InMemoryStateStore<AppSettings.Fragment>();
        var retryTarget = new InMemoryStateStore<AppSettings.Fragment>();
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [
            new("environment", environment, priority: 200),
            new("user", user, priority: 100),
            new("legacy", legacy, priority: 50),
            new("primary", primaryTarget, priority: 0, writer: primaryTarget),
            new("retry-only", retryTarget, priority: -1, writer: retryTarget),
        ]));
        var targets = new Dictionary<string, Func<IConfiglueFragment, IConfiglueFragment>>(StringComparer.Ordinal)
        {
            ["primary"] = static fragment => fragment,
            ["retry-only"] = static fragment => new AppSettings.Fragment
            {
                RetryCount = ((AppSettings.Fragment)fragment).RetryCount,
            },
        };
        IWritableOptions<AppSettings> writableOptions = options;

        var firstRun = await writableOptions.MigrateSourcesToTargetsAsync(["legacy", "user"], targets);
        var primary = await primaryTarget.ReadAsync();
        var retryOnly = await retryTarget.ReadAsync();

        await Assert.That(primary.Value!.RetryCount.Value).IsEqualTo(12);
        await Assert.That(primary.Value.Database.Value!.Host.Value).IsEqualTo("legacy.db");
        await Assert.That(primary.Value.Database.Value.Port.Value).IsEqualTo(6432);
        await Assert.That(primary.Value.Label.IsPresent).IsFalse();
        await Assert.That(primary.Value.Plugins.Value).IsEquivalentTo(["user-plugin"]);
        await Assert.That(retryOnly.Value!.RetryCount.Value).IsEqualTo(12);
        await Assert.That(retryOnly.Value.Database.IsPresent).IsFalse();
        await Assert.That(firstRun.Targets.All(static result => !result.WasAlreadyCurrent)).IsTrue();

        var secondRun = await writableOptions.MigrateSourcesToTargetsAsync(["legacy", "user"], targets);

        await Assert.That(secondRun.Targets.All(static result => result.WasAlreadyCurrent)).IsTrue();
        await Assert.That(secondRun.Targets.Count).IsEqualTo(2);
        await Assert.That(secondRun.Targets.All(static result => result.TargetRevision == "1")).IsTrue();
        await Assert.That(secondRun.SourceIds).IsEquivalentTo(["user", "legacy"]);
    }

    [Test]
    public async Task MigrateSourcesToTargetsAsync_ResumesAfterALaterTargetFails()
    {
        var source = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(22),
        });
        var firstTarget = new InMemoryStateStore<AppSettings.Fragment>();
        var secondTarget = new InMemoryStateStore<AppSettings.Fragment>();
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [
            new("source", source, priority: 100, writer: source),
            new("first-target", firstTarget, priority: 0, writer: firstTarget),
            new("second-target", secondTarget, priority: -1, writer: new FailOnceStateWriter<AppSettings.Fragment>(secondTarget)),
        ]));
        var targets = new Dictionary<string, Func<IConfiglueFragment, IConfiglueFragment>>(StringComparer.Ordinal)
        {
            ["first-target"] = static fragment => fragment,
            ["second-target"] = static fragment => fragment,
        };
        IWritableOptions<AppSettings> writableOptions = options;
        var failed = false;
        try
        {
            await writableOptions.MigrateSourcesToTargetsAsync(["source"], targets, retireSources: true);
        }
        catch (IOException)
        {
            failed = true;
        }

        await Assert.That(failed).IsTrue();
        var afterFailure = await options.ReadAsync();
        await Assert.That(afterFailure.Revisions!.TryGetRevision("source", out _)).IsTrue();
        await Assert.That(afterFailure.Value!.RetryCount).IsEqualTo(22);

        var resumed = await writableOptions.MigrateSourcesToTargetsAsync(["source"], targets, retireSources: true);

        await Assert.That(resumed.Targets[0].WasAlreadyCurrent).IsTrue();
        await Assert.That(resumed.Targets[1].WasAlreadyCurrent).IsFalse();
        await Assert.That(resumed.SourcesRetired).IsTrue();
        await Assert.That(resumed.RetiredSourceIds).IsEquivalentTo(["source"]);
        await Assert.That((await secondTarget.ReadAsync()).Value!.RetryCount.Value).IsEqualTo(22);

        var repeated = await writableOptions.MigrateSourcesToTargetsAsync(["source"], targets, retireSources: true);
        await Assert.That(repeated.Targets.All(static target => target.WasAlreadyCurrent)).IsTrue();
        await Assert.That(repeated.SourcesRetired).IsTrue();
        await Assert.That((await options.ReadAsync()).Value!.RetryCount).IsEqualTo(22);
        await Assert.That((await options.ReadAsync()).Revisions!.TryGetRevision("source", out _)).IsFalse();

        await options.SaveAsync(settings => settings.RetryCount = 23);
        await Assert.That((await source.ReadAsync()).Value!.RetryCount.Value).IsEqualTo(22);
        await Assert.That((await firstTarget.ReadAsync()).Value!.RetryCount.Value).IsEqualTo(23);
        await Assert.That((await options.ReadAsync()).Value!.RetryCount).IsEqualTo(23);
    }

    [Test]
    public async Task MigrateSourcesToTargetsAsync_RefusesRetirementThatWouldChangeEffectiveModel()
    {
        var source = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(22),
        });
        var target = new InMemoryStateStore<AppSettings.Fragment>();
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [
            new("source", source, priority: 100),
            new("target", target, priority: 0, writer: target),
        ]));
        IWritableOptions<AppSettings> writableOptions = options;
        var projections = new Dictionary<string, Func<IConfiglueFragment, IConfiglueFragment>>(StringComparer.Ordinal)
        {
            ["target"] = static _ => new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(25),
            },
        };
        var rejected = false;
        try
        {
            await writableOptions.MigrateSourcesToTargetsAsync(["source"], projections, retireSources: true);
        }
        catch (StateConflictException)
        {
            rejected = true;
        }

        var resolved = await options.ReadAsync();
        await Assert.That(rejected).IsTrue();
        await Assert.That(resolved.Revisions!.TryGetRevision("source", out _)).IsTrue();
        await Assert.That(resolved.Value!.RetryCount).IsEqualTo(22);
        await Assert.That((await target.ReadAsync()).Value!.RetryCount.Value).IsEqualTo(25);
    }

    [Test]
    public async Task SerializedStateSource_ComposesResourceCodecWriterAndWatcherCapabilities()
    {
        var resource = new InMemoryResource();
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "serialized",
            resource,
            new JsonStateCodec<AppSettings.Fragment>(),
            physicalOrigin: "memory://settings");
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([source]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sourceSet);

        await options.ApplyPatchAsync(new AppSettings.Patch
        {
            RetryCount = FragmentOperation<int>.Set(12),
        });
        var resolved = await options.ReadAsync();
        var storedResource = await resource.ReadAsync();

        await Assert.That(source.Writer).IsNotNull();
        await Assert.That(source.Watcher).IsNotNull();
        await Assert.That(resolved.Value!.RetryCount).IsEqualTo(12);
        await Assert.That(resolved.PhysicalOrigin).IsEqualTo("memory://settings");
        await Assert.That(storedResource.Schema).IsEqualTo(AppSettings.ConfiglueSchema.ToMetadata());
    }

    private sealed class FixedStateReader<T>(StateReadResult<T> result) : IStateReader<T>
    {
        public ValueTask<StateReadResult<T>> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailOnceStateWriter<T>(IStateWriter<T> inner) : IStateWriter<T>
    {
        private int _shouldFail = 1;

        public ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _shouldFail, 0) == 1)
            {
                throw new IOException("Simulated transient target failure.");
            }

            return inner.WriteAsync(request, cancellationToken);
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

    private sealed class DatabaseV1ToV2Migration : IStateSchemaMigration<DatabaseSettings.Fragment>
    {
        public StateSchemaMetadata SourceSchema => new("database-settings", 1);

        public StateSchemaMetadata TargetSchema => DatabaseSettings.ConfiglueSchema.ToMetadata();

        public ValueTask<DatabaseSettings.Fragment> MigrateAsync(
            DatabaseSettings.Fragment value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var builder = value.ToBuilder();
            builder.Port = Optional<int>.Present(7400);
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
