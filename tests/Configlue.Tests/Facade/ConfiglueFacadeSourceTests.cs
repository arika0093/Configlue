using System.Buffers;
using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Configlue.Extensions.MSOptions;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Resource.Http;
using Configlue.Source.CommandLine;
using Configlue.Source.Common;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed partial class ConfiglueFacadeSourceTests
{
    [Test]
    public async Task FluentJsonFileRegistrationMountsAndWritesANestedModel()
    {
        using var directory = new TemporaryDirectory();
        var rootPath = Path.Combine(directory.FullPath, "settings.json");
        var databasePath = Path.Combine(directory.FullPath, "database.json");
        await File.WriteAllTextAsync(rootPath, "{\"RetryCount\":3}");
        await File.WriteAllTextAsync(databasePath, "{\"Host\":\"db.example.test\",\"Port\":7443}");

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources
                        .JsonFile(rootPath)
                        .Named(SourceKey<AppSettings>.Named("root-settings"))
                        .ReadOnly()
                        .FallbackWhen(StateFallbackCondition.NotFoundOrUnavailable)
                        .WatchChanges(false);
                    sources
                        .JsonFile(databasePath)
                        .Mount(settings => settings.Database)
                        .Priority(100)
                        .WatchChanges(false);
                })
            );
        });

        var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();
        (
            options.GetDiagnostics().Sources.Any(static source => source.Id == "root-settings")
        ).ShouldBeTrue();
        var current = await options.GetValueAsync();
        (current.RetryCount).ShouldBe(3);
        (current.Database!.Host).ShouldBe("db.example.test");
        (current.Database.Port).ShouldBe(7443);

        await options.SaveAsync(settings => settings.Database!.Host = "updated.example.test");

        var written = JsonNode.Parse(await File.ReadAllTextAsync(databasePath))!;
        (written["Host"]!.GetValue<string>()).ShouldBe("updated.example.test");
        (written["Port"]!.GetValue<int>()).ShouldBe(7443);

        var explicitPatch = new AppSettings.Patch();
        explicitPatch.Database.Host = "selected.example.test";
        await options
            .Source(JsonFileSource.At(databasePath, mountPath: "Database"))
            .SaveAsync(explicitPatch);
        (
            JsonNode.Parse(await File.ReadAllTextAsync(databasePath))!["Host"]!.GetValue<string>()
        ).ShouldBe("selected.example.test");
    }

    [Test]
    public async Task JsonFileSourceUsesThePersistentModelVersionBackupDirectory()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        var backupRoot = Path.Combine(directory.FullPath, "user-state");
        await File.WriteAllTextAsync(path, "{\"Label\":\"before\"}");

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.UseJsonFile(
                    new JsonFileSourceOptions
                    {
                        Path = path,
                        WatchChanges = false,
                        ResourceOptions = new FileResourceOptions
                        {
                            BackupRootDirectory = backupRoot,
                        },
                    }
                )
            );
        });

        var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();
        await options.SaveAsync(settings => settings.Label = "after");

        var backupDirectory = Path.Combine(backupRoot, "configlue-backups", "app-settings.v2");
        var backupPath = Directory.GetFiles(backupDirectory, "settings.json.*.bak").Single();
        (await File.ReadAllTextAsync(backupPath)).ShouldBe("{\"Label\":\"before\"}");
    }

    [Test]
    public async Task JsonFileSelectorResolvesThePathDerivedSourceIdentity()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        await File.WriteAllTextAsync(path, "{\"Label\":\"before\"}");

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Path = path,
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                )
            );
        });

        await context
            .GetAdvancedOptions<AppSettings>()
            .Source(JsonFileSource.At(path))
            .SaveAsync(new AppSettings.Patch { Label = FragmentOperation<string?>.Set("after") });

        (JsonNode.Parse(await File.ReadAllTextAsync(path))!["Label"]!.GetValue<string>()).ShouldBe(
            "after"
        );
    }

    [Test]
    public async Task ProviderSourceRegistration_SharesCommonFluentIdentityAndRoutingSettings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        var readOnlyPath = Path.Combine(directory.FullPath, "defaults.json");
        await File.WriteAllTextAsync(path, "{\"Label\":\"configured\"}");
        await File.WriteAllTextAsync(readOnlyPath, "{\"RetryCount\":3}");

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources
                        .FromJsonFile(
                            new JsonFileSourceOptions
                            {
                                Path = path,
                                WatchChanges = false,
                                ResourceOptions = new FileResourceOptions { CreateBackup = false },
                            }
                        )
                        .Named("settings")
                        .Priority(25)
                        .FallbackWhen(StateFallbackCondition.NotFoundOrUnavailable)
                        .Writable()
                        .ExplicitOnly();
                    sources
                        .FromJsonFile(
                            new JsonFileSourceOptions
                            {
                                Path = readOnlyPath,
                                WatchChanges = false,
                                ResourceOptions = new FileResourceOptions { CreateBackup = false },
                            }
                        )
                        .Named("defaults")
                        .Priority(0)
                        .ReadOnly();
                })
            );
        });

        var diagnostics = context.GetAdvancedOptions<AppSettings>().GetDiagnostics();
        var source = diagnostics.Sources.Single(static source => source.Id == "settings");
        var readOnly = diagnostics.Sources.Single(static source => source.Id == "defaults");
        (source.Id).ShouldBe("settings");
        (source.Priority).ShouldBe(25);
        (source.FallbackCondition).ShouldBe(StateFallbackCondition.NotFoundOrUnavailable);
        (source.CanWrite).ShouldBeTrue();
        (readOnly.CanWrite).ShouldBeFalse();
        (diagnostics.DefaultWriteSourceId).ShouldBeNull();
    }

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
    public async Task JsonFacadeFileSource_PreservesJsoncCommentsAndUnknownProperties()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "appsettings.jsonc");
        await File.WriteAllTextAsync(
            path,
            """
            {
              // Root comment.
              "RetryCount": 3,
              "Label": "before", // Inline comment.
              "Unknown": { "Value": "keep" },
              "$value": "keep reserved-looking unknown",
            }
            """
        );

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Path = path,
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                )
            );
        });

        var options = context.GetOptions<AppSettings>();
        (await options.GetValueAsync()).Label.ShouldBe("before");
        await options.SaveAsync(settings => settings.Label = "after");

        var written = await File.ReadAllTextAsync(path);
        written.ShouldContain("// Root comment.");
        written.ShouldContain("// Inline comment.");
        written.ShouldContain("\"Unknown\": { \"Value\": \"keep\" }");
        written.ShouldContain("\"$value\": \"keep reserved-looking unknown\"");
        written.ShouldContain("\"Label\": \"after\"");

        await options.SaveAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Unset }
        );
        written = await File.ReadAllTextAsync(path);
        written.ShouldNotContain("\"RetryCount\"");
        written.ShouldContain("\"Unknown\": { \"Value\": \"keep\" }");
    }

    [Test]
    public async Task JsonFileSource_ReadsJsoncCommentsAndUpdatesDetailedEnvelope()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "appsettings.json");
        var document = """
            {
              // Keep envelope comment.
              "$configlue": { "id": "app-settings", "version": 2 },
              "$value": {
                "RetryCount": 3,
                "Label": "before",
                "Unknown": "keep",
              },
            }
            """;
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(document)).ToArray();
        await File.WriteAllBytesAsync(path, bytes);

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Path = path,
                            WatchChanges = false,
                            DocumentLayout = new DocumentLayoutOptions
                            {
                                Layout = DocumentLayout.Detailed,
                            },
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                )
            );
        });

        var options = context.GetOptions<AppSettings>();
        (await options.GetValueAsync()).Label.ShouldBe("before");
        await options.SaveAsync(settings => settings.Label = "after");

        var written = await File.ReadAllTextAsync(path);
        written.ShouldContain("// Keep envelope comment.");
        written.ShouldContain("\"Label\": \"after\"");
        written.ShouldContain("\"Unknown\": \"keep\"");
        written.ShouldContain("\"RetryCount\": 3");
        (await File.ReadAllBytesAsync(path))
            .AsSpan()
            .StartsWith(Encoding.UTF8.GetPreamble())
            .ShouldBeTrue();
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
        var options = context.GetAdvancedOptions<AppSettings>();
        await options
            .Source(XmlFileSource.At(path, "App:Settings"))
            .SaveAsync(
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("selected") }
            );

        var document = XDocument.Load(path);
        (document.Root!.Element("App")!.Element("Other")!.Element("Value")!.Value).ShouldBe("keep");
        (document.Root!.Element("Root")!.Value).ShouldBe("keep");
        (document.Descendants().Any(element => element.Value == "selected")).ShouldBeTrue();
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
        var options = context.GetAdvancedOptions<AppSettings>();
        await options
            .Source(YamlFileSource.At(path, "App:Settings"))
            .SaveAsync(
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("selected") }
            );

        var yaml = await File.ReadAllTextAsync(path);
        (yaml).ShouldContain("keep");
        (yaml).ShouldContain("selected");
    }

    [Test]
    public async Task YamlFacadeFileSource_PreservesCommentsAndUnknownProperties()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "appsettings.yaml");
        await File.WriteAllTextAsync(
            path,
            """
            # Root comment.
            App:
              # Section comment.
              Settings:
                RetryCount: 3
                Label: before # Inline comment.
                Unknown: keep
              Other:
                Value: keep-sibling
            # Trailing comment.
            """
        );

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromYamlFile(
                        new YamlFileSourceOptions
                        {
                            Path = path,
                            SectionPath = "App:Settings",
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                )
            );
        });

        var options = context.GetOptions<AppSettings>();
        (await options.GetValueAsync()).Label.ShouldBe("before");
        await options.SaveAsync(settings => settings.Label = "after");

        var yaml = await File.ReadAllTextAsync(path);
        yaml.ShouldContain("# Root comment.");
        yaml.ShouldContain("# Section comment.");
        yaml.ShouldContain("# Inline comment.");
        yaml.ShouldContain("# Trailing comment.");
        yaml.ShouldContain("Unknown: keep");
        yaml.ShouldContain("Value: keep-sibling");
        yaml.ShouldContain("Label: after");

        await options.SaveAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Unset }
        );
        yaml = await File.ReadAllTextAsync(path);
        yaml.ShouldNotContain("RetryCount:");
        yaml.ShouldContain("Unknown: keep");
    }

    [Test]
    public async Task YamlFacadeFileSource_PreservesFlowCollectionLayout()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "flow.yaml");
        await File.WriteAllTextAsync(
            path,
            """
            # Keep flow document comment.
            App: { Settings: { RetryCount: 3, Label: before, Unknown: keep }, Other: sibling }
            """
        );

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromYamlFile(
                        new YamlFileSourceOptions
                        {
                            Path = path,
                            SectionPath = "App:Settings",
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                )
            );
        });

        var options = context.GetOptions<AppSettings>();
        (await options.GetValueAsync()).Label.ShouldBe("before");
        await options.SaveAsync(settings => settings.Label = "after");
        await options.SaveAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Unset }
        );

        var yaml = await File.ReadAllTextAsync(path);
        yaml.ShouldContain("# Keep flow document comment.");
        yaml.ShouldContain("Unknown: keep");
        yaml.ShouldContain("Label: after");
        yaml.ShouldNotContain("RetryCount");
        (await options.GetValueAsync()).Label.ShouldBe("after");
    }

    [Test]
    public async Task YamlFacadeRootFileSourcePreservesCommentsAndUnknownProperties()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "root.yaml");
        await File.WriteAllTextAsync(
            path,
            """
            # Root comment.
            RetryCount: 3
            Label: before
            Unknown: keep
            """
        );

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromYamlFile(
                        new YamlFileSourceOptions
                        {
                            Path = path,
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                )
            );
        });

        var options = context.GetOptions<AppSettings>();
        await options.SaveAsync(settings => settings.Label = "after");

        var yaml = await File.ReadAllTextAsync(path);
        yaml.ShouldContain("# Root comment.");
        yaml.ShouldContain("Unknown: keep");
        yaml.ShouldContain("Label: after");
        (await options.GetValueAsync()).Label.ShouldBe("after");
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

        var result = await (
            (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>()
        ).ApplyPatchesAsync([
            new StateSourcePatch(
                "http-settings",
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(2) }
            ),
        ]);
        (result.PhysicalWriteCount).ShouldBe(1);
        return result.Sources.Single().ResourceId;
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
