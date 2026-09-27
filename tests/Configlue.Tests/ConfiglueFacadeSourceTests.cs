using System.Buffers;
using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Resource.Http;
using Configlue.Source.Common;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed class ConfiglueFacadeSourceTests
{
    [Test]
    public async Task JsonFacadeFileSource_WritesNestedSectionAndPreservesSiblings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        await File.WriteAllTextAsync(
            path,
            "{\"App\":{\"Other\":{\"Value\":\"keep\"}},\"Root\":\"keep\"}"
        );

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Id = "json-settings",
                            Path = path,
                            SectionPath = "App:Settings",
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                )
            );
        });

        await context.GetOptions<AppSettings>().SaveAsync(settings => settings.Label = "updated");

        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        (document["App"]!["Other"]!["Value"]!.GetValue<string>()).ShouldBe("keep");
        (document["Root"]!.GetValue<string>()).ShouldBe("keep");
        (FindJsonProperty(document["App"]!["Settings"]!, "Label")!.GetValue<string>()).ShouldBe(
            "updated"
        );
    }

    [Test]
    public async Task XmlFacadeFileSource_WritesNestedSectionAndPreservesSiblings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.xml");
        await File.WriteAllTextAsync(
            path,
            "<root><App><Other><Value>keep</Value></Other></App><Root>keep</Root></root>"
        );

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromXmlFile(
                        new XmlFileSourceOptions
                        {
                            Id = "xml-settings",
                            Path = path,
                            SectionPath = "App:Settings",
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                )
            );
        });

        await context.GetOptions<AppSettings>().SaveAsync(settings => settings.Label = "updated");

        var document = XDocument.Load(path);
        (document.Root!.Element("App")!.Element("Other")!.Element("Value")!.Value).ShouldBe("keep");
        (document.Root!.Element("Root")!.Value).ShouldBe("keep");
        (document.Descendants().Any(element => element.Value == "updated")).ShouldBeTrue();
    }

    [Test]
    public async Task YamlFacadeFileSource_WritesNestedSectionAndPreservesSiblings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.yaml");
        await File.WriteAllTextAsync(path, "App:\n  Other:\n    Value: keep\nRoot: keep\n");

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromYamlFile(
                        new YamlFileSourceOptions
                        {
                            Id = "yaml-settings",
                            Path = path,
                            SectionPath = "App:Settings",
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                )
            );
        });

        await context.GetOptions<AppSettings>().SaveAsync(settings => settings.Label = "updated");

        var yaml = await File.ReadAllTextAsync(path);
        (yaml).ShouldContain("keep");
        (yaml).ShouldContain("updated");
    }

    [Test]
    public async Task FacadeReadOnlyFileSectionRejectsWrites()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "readonly.json");
        await File.WriteAllTextAsync(path, "{\"App\":{\"Settings\":{}}}");

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Id = "readonly-settings",
                            Path = path,
                            SectionPath = "App:Settings",
                            ReadOnly = true,
                            WatchChanges = false,
                        }
                    )
                )
            );
        });

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await context.GetOptions<AppSettings>().SaveAsync(settings => settings.Label = "no")
        );
    }

    [Test]
    public async Task JsonFacadeFileSourceUsesTheSameRegistrationInDependencyInjection()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "di.json");
        await WriteFragmentAsync(
            path,
            new AppSettings.Fragment { Label = Optional<string?>.Present("di-file") }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Id = "di-json",
                            Path = path,
                            WatchChanges = false,
                            ReadOnly = true,
                        }
                    )
                )
            );
        });

        await using var provider = services.BuildServiceProvider();
        (
            provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AppSettings>>()
                .CurrentValue.Label
        ).ShouldBe("di-file");
    }

    [Test]
    public async Task FacadeCreationFailureDisposesEarlierHelperCreatedFileResources()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "first.json");

        Should.Throw<InvalidOperationException>(() =>
            Configlue.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        sources.FromJsonFile(
                            new JsonFileSourceOptions
                            {
                                Id = "first-file",
                                Path = path,
                                WatchChanges = true,
                                ResourceOptions = new FileResourceOptions { CreateBackup = false },
                            }
                        );
                        sources.Add<AppSettings.Fragment>(_ =>
                            throw new InvalidOperationException("factory failed")
                        );
                    })
                );
            })
        );

        await File.WriteAllTextAsync(path, "{}");
        File.Delete(path);
        (Directory.Exists(directory.FullPath)).ShouldBeTrue();
    }

    [Test]
    public async Task ContextShutdownStopsAndDisposesHelperCreatedFileWatcherOnce()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "watched.json");
        var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Id = "watched-json",
                            Path = path,
                            WatchChanges = true,
                            ReadOnly = true,
                        }
                    )
                )
            );
        });
        var resource = context.OwnedResourcesForTests.OfType<FileResource>().Single();
        using var subscription = context.GetOptions<AppSettings>().OnChange(static _ => { });

        await WaitUntilAsync(() => resource.HasActiveWatcherForTests);
        var firstDisposal = context.DisposeAsync().AsTask();
        var secondDisposal = context.DisposeAsync().AsTask();
        await Task.WhenAll(firstDisposal, secondDisposal);

        firstDisposal.ShouldBeSameAs(secondDisposal);
        (resource.IsDisposedForTests).ShouldBeTrue();
        (resource.HasActiveWatcherForTests).ShouldBeFalse();
        (resource.DisposeCallCountForTests).ShouldBe(1);
    }

    [Test]
    public async Task ContextShutdownLeavesCallerSuppliedFileResourceOwnedByCaller()
    {
        using var directory = new TemporaryDirectory();
        var resource = new FileResource(Path.Combine(directory.FullPath, "caller.json"));
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "caller-json",
            resource,
            new JsonStateCodec<AppSettings.Fragment>()
        );
        var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model => model.Sources(sources => sources.Add(source)));
        });
        using var subscription = context.GetOptions<AppSettings>().OnChange(static _ => { });

        await WaitUntilAsync(() => resource.HasActiveWatcherForTests);
        await context.DisposeAsync();

        (resource.IsDisposedForTests).ShouldBeFalse();
        (resource.HasActiveWatcherForTests).ShouldBeTrue();
        (resource.DisposeCallCountForTests).ShouldBe(0);

        resource.Dispose();
        (resource.IsDisposedForTests).ShouldBeTrue();
    }

    [Test]
    public async Task RemovingDynamicOptionsStopsAndDisposesItsHelperCreatedFileWatcherOnce()
    {
        using var directory = new TemporaryDirectory();
        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableDynamicOptions = true;
                model.SourcesForOptions(
                    (name, sources) =>
                        sources.FromJsonFile(
                            new JsonFileSourceOptions
                            {
                                Id = $"dynamic-{name}",
                                Path = Path.Combine(directory.FullPath, $"{name}.json"),
                                ReadOnly = true,
                                WatchChanges = true,
                            }
                        )
                );
            });
        });
        var registry = context.GetOptionsRegistry<AppSettings>();
        registry.TryAdd("late").ShouldBeTrue();
        var resource = ((ConfiglueFacadeOptionsRegistry<AppSettings>)registry)
            .GetOwnedResourcesForTests("late")
            .OfType<FileResource>()
            .Single();
        var handle = registry.Get("late");
        using var subscription = handle.OnChange(static _ => { });

        await WaitUntilAsync(() => resource.HasActiveWatcherForTests);
        (await registry.TryRemoveAsync("late")).ShouldBeTrue();

        (resource.IsDisposedForTests).ShouldBeTrue();
        (resource.HasActiveWatcherForTests).ShouldBeFalse();
        (resource.DisposeCallCountForTests).ShouldBe(1);
        await Should.ThrowAsync<ObjectDisposedException>(async () => await handle.GetValueAsync());
    }

    [Test]
    public async Task RemovingAndDisposingFacadeProfilesStopsAndDisposesTheirFileWatchersOnce()
    {
        using var directory = new TemporaryDirectory();
        var catalogStore = new InMemoryStateStore<ConfiglueProfileCatalog>();
        var catalog = new StateSource<ConfiglueProfileCatalog>(
            "catalog",
            catalogStore,
            writer: catalogStore,
            watcher: catalogStore
        );
        var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.SourcesForOptions(
                    (name, sources) =>
                        sources.FromJsonFile(
                            new JsonFileSourceOptions
                            {
                                Id = $"profile-{name}",
                                Path = Path.Combine(directory.FullPath, $"{name}.json"),
                                WatchChanges = true,
                            }
                        )
                );
            });
        });
        var profiles = context.GetProfiledOptions<AppSettings>();
        await profiles.GetProfileNamesAsync();
        await profiles.CreateProfileAsync("removed", copyFrom: "default");
        await profiles.CreateProfileAsync("context-end", copyFrom: "default");

        var registry =
            (ConfiglueFacadeOptionsRegistry<AppSettings>)context.GetOptionsRegistry<AppSettings>();
        var removedResource = registry
            .GetOwnedResourcesForTests("removed")
            .OfType<FileResource>()
            .Single();
        var contextEndResource = registry
            .GetOwnedResourcesForTests("context-end")
            .OfType<FileResource>()
            .Single();
        using var removedSubscription = context
            .GetOptions<AppSettings>("removed")
            .OnChange(static _ => { });
        using var contextEndSubscription = context
            .GetOptions<AppSettings>("context-end")
            .OnChange(static _ => { });

        await WaitUntilAsync(() =>
            removedResource.HasActiveWatcherForTests && contextEndResource.HasActiveWatcherForTests
        );
        await profiles.RemoveProfileAsync("removed");

        (removedResource.IsDisposedForTests).ShouldBeTrue();
        (removedResource.HasActiveWatcherForTests).ShouldBeFalse();
        (removedResource.DisposeCallCountForTests).ShouldBe(1);

        await context.DisposeAsync();

        (contextEndResource.IsDisposedForTests).ShouldBeTrue();
        (contextEndResource.HasActiveWatcherForTests).ShouldBeFalse();
        (contextEndResource.DisposeCallCountForTests).ShouldBe(1);
    }

    [Test]
    public async Task DynamicSourceFactoryFailureDisposesResourcesCreatedEarlierInItsRuntime()
    {
        var resource = new DisposableProbe();
        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableDynamicOptions = true;
                model.SourcesForOptions(
                    (name, sources) =>
                    {
                        if (name != "failure")
                        {
                            var store = new InMemoryStateStore<AppSettings.Fragment>();
                            sources.Add<AppSettings.Fragment>(
                                _ => new StateSource<AppSettings.Fragment>(
                                    "ordinary",
                                    store,
                                    writer: store,
                                    watcher: store
                                )
                            );
                            return;
                        }

                        sources.Add(new DisposableProbeSourceDefinition(resource));
                        sources.Add<AppSettings.Fragment>(_ =>
                            throw new InvalidOperationException("source factory failed")
                        );
                    }
                );
            });
        });
        var registry = context.GetOptionsRegistry<AppSettings>();

        Should.Throw<InvalidOperationException>(() => registry.TryAdd("failure"));

        (resource.DisposeCallCount).ShouldBe(1);
        registry.TryGet("failure", out _).ShouldBeFalse();
    }

    [Test]
    public async Task CommonSourcePresetWorksInDependencyInjection()
    {
        using var directory = new TemporaryDirectory();
        var selectedPath = Path.Combine(directory.FullPath, "selected.json");
        await WriteFragmentAsync(
            selectedPath,
            new AppSettings.Fragment { Label = Optional<string?>.Present("selected-di") }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.UseCommonSources(
                    new CommonSourceOptions
                    {
                        ApplicationId = $"Configlue.Tests.{Guid.NewGuid():N}",
                        GlobalFileName = "settings.json",
                        SpecificFilePath = selectedPath,
                        WriteLayer = CommonSourceWriteLayer.Specific,
                        EnableGlobalFile = false,
                        EnableLocalFile = false,
                        EnableEnvironment = false,
                        EnableCommandLine = false,
                        FileResourceOptions = new FileResourceOptions { CreateBackup = false },
                    }
                )
            );
        });

        await using var provider = services.BuildServiceProvider();
        (
            provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AppSettings>>()
                .CurrentValue.Label
        ).ShouldBe("selected-di");
    }

    [Test]
    public async Task HttpFacadeSourceReportsEndpointIdentityAndHonorsOverride()
    {
        var endpoint = "https://settings.example.test/app-settings/";
        var defaultId = await WriteHttpPatchAndGetResourceIdAsync(endpoint, resourceId: null);
        using var client = new HttpClient(new NoContentHttpHandler());
        var expected = new HttpResourceReader(client, new Uri(endpoint)).ResourceId;
        (defaultId).ShouldBe(expected);

        var overrideId = new ResourceId("test-resource:settings");
        var actualOverride = await WriteHttpPatchAndGetResourceIdAsync(endpoint, overrideId);
        (actualOverride).ShouldBe(overrideId);
    }

    [Test]
    public async Task JsonHttpSourceUsesNamedClientAndDefaultsToReadOnly()
    {
        var requestedUris = new System.Collections.Concurrent.ConcurrentBag<Uri>();
        var content = SerializeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(11) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services
            .AddHttpClient("json-settings")
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpResponseHandler(requestedUris, content)
            );
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonHttpClientFactory(
                        "json-settings",
                        new JsonHttpSourceOptions
                        {
                            Id = "json-http",
                            EndPoint = "https://settings.example.test/json/",
                            WatchChanges = false,
                        }
                    )
                )
            );
        });

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IWritableOptions<AppSettings>>();
        (await options.GetValueAsync()).RetryCount.ShouldBe(11);
        requestedUris.ShouldContain(new Uri("https://settings.example.test/json/get"));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await options.OpenEditSessionAsync()
        );
    }

    [Test]
    public async Task JsonHttpSourceCanWriteWhenEnabledAndDoesNotOwnTheClient()
    {
        var handler = new RecordingHttpHandler(SerializeFragment(new AppSettings.Fragment()));
        using var client = new HttpClient(handler);
        await using (
            var context = Configlue.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                        sources.FromJsonHttp(
                            new JsonHttpSourceOptions
                            {
                                Id = "writable-json-http",
                                EndPoint = "https://settings.example.test/writable/",
                                Client = client,
                                Writable = true,
                                WatchChanges = false,
                            }
                        )
                    )
                );
            })
        )
        {
            var result = await context
                .GetOptions<AppSettings>()
                .ApplyPatchesAsync([
                    new StateSourcePatch(
                        "writable-json-http",
                        new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(3) }
                    ),
                ]);
            (result.PhysicalWriteCount).ShouldBe(1);
            handler.RequestMethods.ShouldContain(HttpMethod.Put);
        }

        handler.IsDisposed.ShouldBeFalse();
        var response = await client.GetAsync("https://settings.example.test/after-dispose");
        (response.StatusCode).ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task HttpSourceUsesNamedFactoryClientsAndKeepsReadOnlyCapabilities()
    {
        var requestedUris = new System.Collections.Concurrent.ConcurrentBag<Uri>();
        var firstBody = SerializeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) }
        );
        var secondBody = SerializeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services
            .AddHttpClient("settings-primary")
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpResponseHandler(requestedUris, firstBody)
            );
        services
            .AddHttpClient("settings-secondary")
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpResponseHandler(requestedUris, secondBody)
            );
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromHttpClientFactory(
                        "settings-primary",
                        new HttpSourceOptions
                        {
                            Id = "primary",
                            EndPoint = "https://settings.example.test/primary/",
                            Codec = new JsonStateCodec<AppSettings.Fragment>(),
                        }
                    );
                    sources.FromHttpClientFactory(
                        "settings-secondary",
                        new HttpSourceOptions
                        {
                            Id = "secondary",
                            EndPoint = "https://settings.example.test/secondary/",
                            Codec = new JsonStateCodec<AppSettings.Fragment>(),
                            Priority = 10,
                        }
                    );
                })
            );
        });

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IWritableOptions<AppSettings>>();
        (await options.GetValueAsync()).RetryCount.ShouldBe(9);
        requestedUris.ShouldContain(new Uri("https://settings.example.test/primary/get"));
        requestedUris.ShouldContain(new Uri("https://settings.example.test/secondary/get"));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await options.OpenEditSessionAsync();
        });
    }

    private static async Task<ResourceId?> WriteHttpPatchAndGetResourceIdAsync(
        string endpoint,
        ResourceId? resourceId
    )
    {
        using var client = new HttpClient(new NoContentHttpHandler());
        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromHttp(
                        new HttpSourceOptions
                        {
                            Id = "http-settings",
                            EndPoint = endpoint,
                            Client = client,
                            Codec = new JsonStateCodec<AppSettings.Fragment>(),
                            Writable = true,
                            WatchChanges = false,
                            ResourceId = resourceId,
                        }
                    )
                )
            );
        });

        var result = await context
            .GetOptions<AppSettings>()
            .ApplyPatchesAsync([
                new StateSourcePatch(
                    "http-settings",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(2) }
                ),
            ]);
        (result.PhysicalWriteCount).ShouldBe(1);
        return result.Sources.Single().ResourceId;
    }

    [Test]
    public async Task CommonSourcePreset_OrdersSparseLayersAndRoutesWritesToSelectedFile()
    {
        using var directory = new TemporaryDirectory();
        var appId = $"Configlue.Tests.{Guid.NewGuid():N}";
        var globalPath = Path.Combine(
            ConfiglueStandardPaths.GetStandardSaveDirectory(appId),
            "settings.json"
        );
        var localPath = Path.Combine(directory.FullPath, "local.json");
        var specificPath = Path.Combine(directory.FullPath, "selected.json");
        try
        {
            await WriteFragmentAsync(
                globalPath,
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
            );
            await WriteFragmentAsync(
                localPath,
                new AppSettings.Fragment { Label = Optional<string?>.Present("local") }
            );
            await WriteFragmentAsync(
                specificPath,
                new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) }
            );

            var retryOption = new Option<int>("--retry");
            var settingsFileOption = new Option<string>("--settings");
            var rootCommand = new RootCommand();
            rootCommand.Options.Add(retryOption);
            rootCommand.Options.Add(settingsFileOption);
            var parseResult = rootCommand.Parse(["--settings", specificPath, "--retry", "5"]);
            var commonOptions = new CommonSourceOptions
            {
                ApplicationId = appId,
                GlobalFileName = "settings.json",
                LocalFilePath = localPath,
                SpecificFilePath = specificPath,
                EnvironmentPrefix = "CONFIGLUE_TEST",
                EnvironmentVariables = () =>
                    [new KeyValuePair<string, string?>("CONFIGLUE_TEST__RetryCount", "4")],
                CommandLineParseResult = parseResult,
                ConfigureCommandLineMappings = mappings => mappings.Map(retryOption, "RetryCount"),
                WriteLayer = CommonSourceWriteLayer.Specific,
                FileResourceOptions = new FileResourceOptions { CreateBackup = false },
            };

            await using var context = Configlue.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model => model.UseCommonSources(commonOptions));
            });
            var options = context.GetOptions<AppSettings>();
            var value = await options.GetValueAsync();
            (value.RetryCount).ShouldBe(5);
            (value.Label).ShouldBe("local");
            (value.Enabled).ShouldBeFalse();
            (await options.ExplainAsync("RetryCount")).HighestPrioritySourceId.ShouldBe(
                "common.commandLine"
            );

            await options.SaveAsync(settings => settings.Label = "written-to-specific");
            var written = new JsonStateCodec<AppSettings.Fragment>();
            var selected = await File.ReadAllBytesAsync(specificPath);
            var sequence = new ReadOnlySequence<byte>(selected);
            var selectedFragment = written.Deserialize(in sequence, default)!;
            (selectedFragment.Label.Value).ShouldBe("written-to-specific");
            await Should.ThrowAsync<StateConflictException>(async () =>
                await options.SaveAsync(settings => settings.RetryCount = 8)
            );
        }
        finally
        {
            if (File.Exists(globalPath))
            {
                File.Delete(globalPath);
                var appDirectory = Path.GetDirectoryName(globalPath)!;
                if (!Directory.EnumerateFileSystemEntries(appDirectory).Any())
                {
                    Directory.Delete(appDirectory);
                }
            }
        }
    }

    private static async Task WriteFragmentAsync(string path, AppSettings.Fragment fragment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var output = new ArrayBufferWriter<byte>();
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        codec.Serialize(fragment, output, default);
        await File.WriteAllBytesAsync(path, output.WrittenMemory.ToArray());
    }

    private static byte[] SerializeFragment(AppSettings.Fragment fragment)
    {
        var output = new ArrayBufferWriter<byte>();
        new JsonStateCodec<AppSettings.Fragment>().Serialize(fragment, output, default);
        return output.WrittenSpan.ToArray();
    }

    private static JsonNode? FindJsonProperty(JsonNode node, string propertyName)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue(propertyName, out var value))
            {
                return value;
            }
            foreach (var child in obj.Select(static pair => pair.Value))
            {
                if (child is not null && FindJsonProperty(child, propertyName) is { } found)
                {
                    return found;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null && FindJsonProperty(child, propertyName) is { } found)
                {
                    return found;
                }
            }
        }
        return null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            FullPath = Path.Combine(
                Path.GetTempPath(),
                "Configlue.Tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(FullPath))
            {
                Directory.Delete(FullPath, recursive: true);
            }
        }
    }

    private sealed class DisposableProbe : IDisposable
    {
        public int DisposeCallCount { get; private set; }

        public void Dispose() => DisposeCallCount++;
    }

    private sealed class DisposableProbeSourceDefinition(DisposableProbe resource)
        : IConfiglueSourceDefinition
    {
        public StateSource<TFragment> Create<TFragment>(
            ConfiglueModelSchema modelSchema,
            IServiceProvider? serviceProvider,
            Action<IDisposable> ownResource
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ownResource(resource);
            var store = new InMemoryStateStore<TFragment>();
            return new StateSource<TFragment>("owned-probe", store, writer: store, watcher: store);
        }
    }

    private sealed class NoContentHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.NoContent)
            );
    }

    private sealed class RecordingHttpHandler(byte[] content) : HttpMessageHandler
    {
        public List<HttpMethod> RequestMethods { get; } = [];

        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestMethods.Add(request.Method);
            return Task.FromResult(
                request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(content),
                    }
                    : new HttpResponseMessage(HttpStatusCode.NoContent)
            );
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class HttpResponseHandler(
        System.Collections.Concurrent.ConcurrentBag<Uri> requestedUris,
        byte[] content
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.RequestUri is { } requestUri)
            {
                requestedUris.Add(requestUri);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        }
    }
}
