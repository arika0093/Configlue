using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.Provider.MessagePack;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;

namespace Configlue.Tests;

/// <summary>
/// MessagePack leg of the representation migration matrix (#259). The same generic
/// fragment pipeline carries JSON/YAML/XML/MessagePack conversions without format-specific
/// migration logic; binary payloads simply round-trip through their own codec.
/// </summary>
public sealed class MessagePackRepresentationMigrationTests
{
    [Test]
    public async Task JsonToMessagePack_MigratesThroughGenericFragmentPipeline()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.json");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.msgpack");
        await SeedJsonAsync(legacyPath, retryCount: 7, label: "to-msgpack");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromMessagePackFile(MessagePackOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromJsonFile(JsonOptions("legacy", legacyPath))
                    );
                });
            });
        });

        await context
            .GetSources<AppSettings>()
            .MigrateSourceAsync(SourceId.From("legacy"), SourceId.From("canonical"));

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(7);
        (value.Label).ShouldBe("to-msgpack");
        var bytes = await File.ReadAllBytesAsync(canonicalPath);
        (bytes.Length > 0).ShouldBeTrue();
        Should.Throw<JsonException>(() => JsonDocument.Parse(bytes));
    }

    [Test]
    public async Task MessagePackToJson_MigratesThroughGenericFragmentPipeline()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.msgpack");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");
        await SeedMessagePackAsync(legacyPath, retryCount: 3, label: "from-msgpack");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromJsonFile(JsonOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromMessagePackFile(MessagePackOptions("legacy", legacyPath))
                    );
                });
            });
        });

        await context
            .GetSources<AppSettings>()
            .MigrateSourceAsync(SourceId.From("legacy"), SourceId.From("canonical"));

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(3);
        (value.Label).ShouldBe("from-msgpack");
        (await File.ReadAllTextAsync(canonicalPath)).ShouldContain("{");
    }

    [Test]
    public async Task YamlToMessagePack_MigratesThroughGenericFragmentPipeline()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.yaml");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.msgpack");
        await SeedYamlAsync(legacyPath, retryCount: 9, label: "yaml-to-msgpack");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromMessagePackFile(MessagePackOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromYamlFile(YamlOptions("legacy", legacyPath))
                    );
                });
            });
        });

        await context
            .GetSources<AppSettings>()
            .MigrateSourceAsync(SourceId.From("legacy"), SourceId.From("canonical"));

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(9);
        (value.Label).ShouldBe("yaml-to-msgpack");
    }

    [Test]
    public async Task XmlToMessagePack_MigratesThroughGenericFragmentPipeline()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.xml");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.msgpack");
        await SeedXmlAsync(legacyPath, retryCount: 5, label: "xml-to-msgpack");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromMessagePackFile(MessagePackOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromXmlFile(XmlOptions("legacy", legacyPath))
                    );
                });
            });
        });

        await context
            .GetSources<AppSettings>()
            .MigrateSourceAsync(SourceId.From("legacy"), SourceId.From("canonical"));

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(5);
        (value.Label).ShouldBe("xml-to-msgpack");
    }

    private static JsonFileSourceOptions JsonOptions(string id, string path) =>
        new()
        {
            Id = id,
            Path = path,
            WatchChanges = false,
            DocumentLayout = new DocumentLayoutOptions { ModelId = "app-settings" },
            ResourceOptions = new FileResourceOptions { CreateBackup = false },
        };

    private static YamlFileSourceOptions YamlOptions(string id, string path) =>
        new()
        {
            Id = id,
            Path = path,
            WatchChanges = false,
            DocumentLayout = new DocumentLayoutOptions { ModelId = "app-settings" },
            ResourceOptions = new FileResourceOptions { CreateBackup = false },
        };

    private static XmlFileSourceOptions XmlOptions(string id, string path) =>
        new()
        {
            Id = id,
            Path = path,
            WatchChanges = false,
            ResourceOptions = new FileResourceOptions { CreateBackup = false },
        };

    private static MessagePackFileSourceOptions MessagePackOptions(string id, string path) =>
        new()
        {
            Id = id,
            Path = path,
            WatchChanges = false,
            ResourceOptions = new FileResourceOptions { CreateBackup = false },
        };

    private static async Task SeedJsonAsync(string path, int retryCount, string label)
    {
        await using var seed = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.FromJsonFile(JsonOptions("seed", path)))
            );
        });
        var state = seed.GetState<AppSettings>();
        await state.SaveAsync(settings => settings.RetryCount = retryCount);
        await state.SaveAsync(settings => settings.Label = label);
    }

    private static async Task SeedYamlAsync(string path, int retryCount, string label)
    {
        await using var seed = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.FromYamlFile(YamlOptions("seed", path)))
            );
        });
        var state = seed.GetState<AppSettings>();
        await state.SaveAsync(settings => settings.RetryCount = retryCount);
        await state.SaveAsync(settings => settings.Label = label);
    }

    private static async Task SeedXmlAsync(string path, int retryCount, string label)
    {
        await using var seed = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.FromXmlFile(XmlOptions("seed", path)))
            );
        });
        var state = seed.GetState<AppSettings>();
        await state.SaveAsync(settings => settings.RetryCount = retryCount);
        await state.SaveAsync(settings => settings.Label = label);
    }

    private static async Task SeedMessagePackAsync(string path, int retryCount, string label)
    {
        await using var seed = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromMessagePackFile(MessagePackOptions("seed", path))
                )
            );
        });
        var state = seed.GetState<AppSettings>();
        await state.SaveAsync(settings => settings.RetryCount = retryCount);
        await state.SaveAsync(settings => settings.Label = label);
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
}
