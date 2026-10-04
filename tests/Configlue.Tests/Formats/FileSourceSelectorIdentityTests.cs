using Configlue.Provider.Json;
using Configlue.Provider.Yaml;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class FileSourceSelectorIdentityTests
{
    [Test]
    public void JsonSelector_KeepsAsciiAndFullwidthPathsDistinct()
    {
        using var directory = new TemporaryDirectory();
        var ascii = Path.Combine(directory.FullPath, "review-A.json");
        var fullwidth = Path.Combine(directory.FullPath, "review-Ａ.json");

        var asciiId = JsonFileSourceSelector.CreateSourceId(ascii, null);
        var fullwidthId = JsonFileSourceSelector.CreateSourceId(fullwidth, null);

        (asciiId == fullwidthId).ShouldBeFalse();
    }

    [Test]
    public void YamlSelector_KeepsAsciiAndFullwidthPathsDistinct()
    {
        using var directory = new TemporaryDirectory();
        var ascii = Path.Combine(directory.FullPath, "review-A.yaml");
        var fullwidth = Path.Combine(directory.FullPath, "review-Ａ.yaml");

        var asciiId = YamlFileSourceSelector.CreateSourceId(ascii, null);
        var fullwidthId = YamlFileSourceSelector.CreateSourceId(fullwidth, null);

        (asciiId == fullwidthId).ShouldBeFalse();
    }

    [Test]
    public async Task JsonFacade_RegistersAsciiAndFullwidthFilesIndependently()
    {
        using var directory = new TemporaryDirectory();
        var asciiPath = Path.Combine(directory.FullPath, "review-A.json");
        var fullwidthPath = Path.Combine(directory.FullPath, "review-Ａ.json");
        await File.WriteAllTextAsync(asciiPath, """{"RetryCount": 1}""");
        await File.WriteAllTextAsync(fullwidthPath, """{"RetryCount": 2}""");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Path = asciiPath,
                            ReadOnly = true,
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    );
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Path = fullwidthPath,
                            Priority = 10,
                            ReadOnly = true,
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    );
                })
            );
        });

        var diagnostics = context.GetRuntimeState<AppSettings>().GetDiagnostics();
        var asciiId = SourceId.From(JsonFileSourceSelector.CreateSourceId(asciiPath, null));
        var fullwidthId = SourceId.From(
            JsonFileSourceSelector.CreateSourceId(fullwidthPath, null)
        );
        (asciiId == fullwidthId).ShouldBeFalse();
        (diagnostics.Sources.Any(source => source.Id == asciiId)).ShouldBeTrue();
        (diagnostics.Sources.Any(source => source.Id == fullwidthId)).ShouldBeTrue();
        (diagnostics.Sources.Count).ShouldBe(2);

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(2);
    }

    [Test]
    public async Task YamlFacade_RegistersAsciiAndFullwidthFilesIndependently()
    {
        using var directory = new TemporaryDirectory();
        var asciiPath = Path.Combine(directory.FullPath, "review-A.yaml");
        var fullwidthPath = Path.Combine(directory.FullPath, "review-Ａ.yaml");
        await File.WriteAllTextAsync(asciiPath, "RetryCount: 1\n");
        await File.WriteAllTextAsync(fullwidthPath, "RetryCount: 2\n");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromYamlFile(
                        new YamlFileSourceOptions
                        {
                            Path = asciiPath,
                            ReadOnly = true,
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    );
                    sources.FromYamlFile(
                        new YamlFileSourceOptions
                        {
                            Path = fullwidthPath,
                            Priority = 10,
                            ReadOnly = true,
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    );
                })
            );
        });

        var diagnostics = context.GetRuntimeState<AppSettings>().GetDiagnostics();
        (diagnostics.Sources.Count).ShouldBe(2);

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(2);
    }

    [Test]
    public void JsonSelector_KeepsDistinctWireSectionKeysSeparate()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");

        var ascii = JsonFileSource.At(path, "A").SourceId;
        var fullwidth = JsonFileSource.At(path, "Ａ").SourceId;
        var ligature = JsonFileSourceSelector.CreateSourceId(path, "ﬁ");
        var expanded = JsonFileSourceSelector.CreateSourceId(path, "fi");
        var composed = JsonFileSourceSelector.CreateSourceId(path, "é");
        var decomposed = JsonFileSourceSelector.CreateSourceId(path, "é");

        (ascii == fullwidth).ShouldBeFalse();
        (ligature == expanded).ShouldBeFalse();
        (composed == decomposed).ShouldBeFalse();
    }

    [Test]
    public void YamlSelector_KeepsDistinctWireSectionKeysSeparate()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.yaml");

        var ascii = YamlFileSource.At(path, "A").SourceId;
        var fullwidth = YamlFileSource.At(path, "Ａ").SourceId;
        var ligature = YamlFileSourceSelector.CreateSourceId(path, "ﬁ");
        var expanded = YamlFileSourceSelector.CreateSourceId(path, "fi");
        var composed = YamlFileSourceSelector.CreateSourceId(path, "é");
        var decomposed = YamlFileSourceSelector.CreateSourceId(path, "é");

        (ascii == fullwidth).ShouldBeFalse();
        (ligature == expanded).ShouldBeFalse();
        (composed == decomposed).ShouldBeFalse();
    }

    [Test]
    public async Task JsonFacade_SelectsDistinctWireKeySectionsSeparately()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """{"A": {"RetryCount": 1}, "Ａ": {"RetryCount": 2}}"""
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Path = path,
                            SectionPath = "A",
                            ReadOnly = true,
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    );
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Path = path,
                            SectionPath = "Ａ",
                            Priority = 10,
                            ReadOnly = true,
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    );
                })
            );
        });

        var diagnostics = context.GetRuntimeState<AppSettings>().GetDiagnostics();
        (diagnostics.Sources.Count).ShouldBe(2);

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(2);
    }

    [Test]
    public async Task YamlFacade_SelectsDistinctWireKeySectionsSeparately()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.yaml");
        await File.WriteAllTextAsync(path, "A:\n  RetryCount: 1\nＡ:\n  RetryCount: 2\n");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromYamlFile(
                        new YamlFileSourceOptions
                        {
                            Path = path,
                            SectionPath = "A",
                            ReadOnly = true,
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    );
                    sources.FromYamlFile(
                        new YamlFileSourceOptions
                        {
                            Path = path,
                            SectionPath = "Ａ",
                            Priority = 10,
                            ReadOnly = true,
                            WatchChanges = false,
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    );
                })
            );
        });

        var diagnostics = context.GetRuntimeState<AppSettings>().GetDiagnostics();
        (diagnostics.Sources.Count).ShouldBe(2);

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(2);
    }

    [Test]
    public void Selectors_TreatEquivalentSectionSyntaxEqually()
    {
        using var directory = new TemporaryDirectory();
        var jsonPath = Path.Combine(directory.FullPath, "settings.json");
        var yamlPath = Path.Combine(directory.FullPath, "settings.yaml");

        var colon = JsonFileSourceSelector.CreateSourceId(jsonPath, "App:Settings");
        var underscores = JsonFileSourceSelector.CreateSourceId(jsonPath, "App__Settings");
        var spaced = JsonFileSourceSelector.CreateSourceId(jsonPath, "  App : Settings  ");
        (colon == underscores).ShouldBeTrue();
        (colon == spaced).ShouldBeTrue();

        var yamlColon = YamlFileSourceSelector.CreateSourceId(yamlPath, "App:Settings");
        var yamlUnderscores = YamlFileSourceSelector.CreateSourceId(yamlPath, "App__Settings");
        var yamlSpaced = YamlFileSourceSelector.CreateSourceId(yamlPath, "  App : Settings  ");
        (yamlColon == yamlUnderscores).ShouldBeTrue();
        (yamlColon == yamlSpaced).ShouldBeTrue();

        var different = JsonFileSourceSelector.CreateSourceId(jsonPath, "App:Other");
        (colon == different).ShouldBeFalse();
    }

    [Test]
    public void JsonSelector_KeepsMountPathsDistinct()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");

        var ascii = JsonFileSourceSelector.CreateSourceId(path, null, "Mount");
        var fullwidth = JsonFileSourceSelector.CreateSourceId(path, null, "Ｍount");
        (ascii == fullwidth).ShouldBeFalse();

        var trimmed = JsonFileSourceSelector.CreateSourceId(path, null, "  Mount  ");
        (ascii == trimmed).ShouldBeTrue();
    }

    [Test]
    public void Selectors_TreatRelativeAndAbsolutePathsEqually()
    {
        using var directory = new TemporaryDirectory();
        var absoluteJson = Path.Combine(directory.FullPath, "settings.json");
        var relativeJson = Path.GetRelativePath(
            Directory.GetCurrentDirectory(),
            absoluteJson
        );
        (
            JsonFileSourceSelector.CreateSourceId(absoluteJson, null)
                == JsonFileSourceSelector.CreateSourceId(relativeJson, null)
        ).ShouldBeTrue();

        var absoluteYaml = Path.Combine(directory.FullPath, "settings.yaml");
        var relativeYaml = Path.GetRelativePath(
            Directory.GetCurrentDirectory(),
            absoluteYaml
        );
        (
            YamlFileSourceSelector.CreateSourceId(absoluteYaml, null)
                == YamlFileSourceSelector.CreateSourceId(relativeYaml, null)
        ).ShouldBeTrue();
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
