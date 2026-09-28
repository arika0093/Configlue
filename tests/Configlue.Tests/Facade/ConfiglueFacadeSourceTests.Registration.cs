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

        await using var context = ConfiglueApp.CreateContext(builder =>
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

        var options = (IConfiglueRuntimeOptions<AppSettings>)context.GetOptions<AppSettings>();
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

        await using var context = ConfiglueApp.CreateContext(builder =>
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

        var options = (IConfiglueRuntimeOptions<AppSettings>)context.GetOptions<AppSettings>();
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

        await using var context = ConfiglueApp.CreateContext(builder =>
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
            .GetRuntimeOptions<AppSettings>()
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

        await using var context = ConfiglueApp.CreateContext(builder =>
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

        var diagnostics = context.GetRuntimeOptions<AppSettings>().GetDiagnostics();
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

        await using var context = ConfiglueApp.CreateContext(builder =>
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

        await using var context = ConfiglueApp.CreateContext(builder =>
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

        await using var context = ConfiglueApp.CreateContext(builder =>
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

        await using var context = ConfiglueApp.CreateContext(builder =>
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
        var options = context.GetRuntimeOptions<AppSettings>();
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

        await using var context = ConfiglueApp.CreateContext(builder =>
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
        var options = context.GetRuntimeOptions<AppSettings>();
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

        await using var context = ConfiglueApp.CreateContext(builder =>
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

        await using var context = ConfiglueApp.CreateContext(builder =>
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

        await using var context = ConfiglueApp.CreateContext(builder =>
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

        await using var context = ConfiglueApp.CreateContext(builder =>
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
}
