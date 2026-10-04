using Configlue;
using Configlue.CompilerServices;
using Configlue.Provider.Json;
using Configlue.Provider.Yaml;

namespace Configlue.Tests;

[ConfiglueModel("example.parity-leaf-256", Version = 1)]
public partial class ParityLeaf256
{
    public int Value { get; set; }
}

[ConfiglueModel("example.parity-root-256", Version = 1)]
public partial class ParityRoot256
{
    public string Name { get; set; } = string.Empty;

    public int Count { get; set; }

    public ParityLeaf256? Child { get; set; }

    public List<int> Scores { get; set; } = [];

    public string? Optional { get; set; }
}

[ConfiglueModel("example.parity-node-256", Version = 1)]
public partial class ParityNode256
{
    public int Value { get; set; }

    public ParityNode256? Next { get; set; }
}

[ConfiglueModel("example.parity-dag-256", Version = 1)]
public partial class ParityDag256
{
    public int Value { get; set; }

    public ParityLeaf256? Left { get; set; }

    public ParityLeaf256? Right { get; set; }
}

public sealed class DocumentSemanticParity256Tests
{
    [Test]
    public async Task Add_Set_Unset_Parity()
    {
        using var directory = new TemporaryDirectory();
        var jsonPath = Path.Combine(directory.FullPath, "root.json");
        var yamlPath = Path.Combine(directory.FullPath, "root.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(
            jsonPath,
            """
            {
              // Root comment.
              "$version": 1,
              "Name": "base",
              "Unknown": "keep"
            }
            """
        );
        await File.WriteAllTextAsync(
            yamlPath,
            "$version: 1\nName: base\n# Root comment.\nUnknown: keep\n"
        );

        await using (var json = CreateJsonContext<ParityRoot256>(jsonPath))
        {
            await json.GetState<ParityRoot256>().SaveAsync(settings => settings.Count = 7);
        }

        await using (var yaml = CreateYamlContext<ParityRoot256>(yamlPath))
        {
            await yaml.GetState<ParityRoot256>().SaveAsync(settings => settings.Count = 7);
        }

        var jsonWritten = await File.ReadAllTextAsync(jsonPath);
        var yamlWritten = await File.ReadAllTextAsync(yamlPath);
        jsonWritten.Replace(" ", string.Empty).ShouldContain("\"Count\":7");
        jsonWritten.ShouldContain("\"Unknown\"");
        jsonWritten.ShouldContain("Root comment.");
        yamlWritten.ShouldContain("Count: 7");
        yamlWritten.ShouldContain("Unknown");
        yamlWritten.ShouldContain("Root comment.");

        await using (var json = CreateJsonContext<ParityRoot256>(jsonPath))
        {
            await json.GetState<ParityRoot256>().SaveAsync(patch => patch.Optional.Unset());
        }

        await using (var yaml = CreateYamlContext<ParityRoot256>(yamlPath))
        {
            await yaml.GetState<ParityRoot256>().SaveAsync(patch => patch.Optional.Unset());
        }

        jsonWritten = await File.ReadAllTextAsync(jsonPath);
        yamlWritten = await File.ReadAllTextAsync(yamlPath);
        jsonWritten.ShouldNotContain("\"Optional\"");
        jsonWritten.ShouldContain("\"Unknown\"");
        yamlWritten.ShouldNotContain("Optional:");
        yamlWritten.ShouldContain("Unknown");
    }

    [Test]
    public async Task Nested_Parity()
    {
        using var directory = new TemporaryDirectory();
        var jsonPath = Path.Combine(directory.FullPath, "nested.json");
        var yamlPath = Path.Combine(directory.FullPath, "nested.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(
            jsonPath,
            """{"$version":1,"Name":"base","Child":{"Value":1},"Unknown":"keep"}"""
        );
        await File.WriteAllTextAsync(
            yamlPath,
            "$version: 1\nName: base\nChild:\n  Value: 1\nUnknown: keep\n"
        );

        await using (var json = CreateJsonContext<ParityRoot256>(jsonPath))
        {
            await json.GetState<ParityRoot256>().SaveAsync(settings => settings.Child!.Value = 2);
        }

        await using (var yaml = CreateYamlContext<ParityRoot256>(yamlPath))
        {
            await yaml.GetState<ParityRoot256>().SaveAsync(settings => settings.Child!.Value = 2);
        }

        var jsonWritten = await File.ReadAllTextAsync(jsonPath);
        var yamlWritten = await File.ReadAllTextAsync(yamlPath);
        jsonWritten.Replace(" ", string.Empty).ShouldContain("\"Value\":2");
        jsonWritten.ShouldContain("\"Unknown\"");
        yamlWritten.ShouldContain("Value: 2");
        yamlWritten.ShouldContain("Unknown");
    }

    [Test]
    public async Task FiniteRecursive_Parity()
    {
        using var directory = new TemporaryDirectory();
        var jsonPath = Path.Combine(directory.FullPath, "node.json");
        var yamlPath = Path.Combine(directory.FullPath, "node.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(
            jsonPath,
            """
            {
              // Root comment.
              "$version": 1,
              "Value": 1,
              "Next": {
                "Value": 2,
                "Next": null,
                "DeepUnknown": { "Kept": true }
              }
            }
            """
        );
        await File.WriteAllTextAsync(
            yamlPath,
            "$version: 1\nValue: 1\n# Root comment.\nNext:\n  Value: 2\n  Next: null\n  DeepUnknown:\n    Kept: true\n"
        );

        await using (var json = CreateJsonContext<ParityNode256>(jsonPath))
        {
            await json.GetState<ParityNode256>().SaveAsync(settings => settings.Value = 10);
        }

        await using (var yaml = CreateYamlContext<ParityNode256>(yamlPath))
        {
            await yaml.GetState<ParityNode256>().SaveAsync(settings => settings.Value = 10);
        }

        var jsonWritten = await File.ReadAllTextAsync(jsonPath);
        var yamlWritten = await File.ReadAllTextAsync(yamlPath);
        jsonWritten.Replace(" ", string.Empty).ShouldContain("\"Value\":10");
        jsonWritten.ShouldContain("\"DeepUnknown\"");
        jsonWritten.ShouldContain("Root comment.");
        yamlWritten.ShouldContain("Value: 10");
        yamlWritten.ShouldContain("DeepUnknown");
        yamlWritten.ShouldContain("Root comment.");
    }

    [Test]
    public async Task SharedDag_Parity()
    {
        using var directory = new TemporaryDirectory();
        var jsonPath = Path.Combine(directory.FullPath, "dag.json");
        var yamlPath = Path.Combine(directory.FullPath, "dag.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(
            jsonPath,
            """{"$version":1,"Value":0,"Left":{"Value":1},"Right":{"Value":2},"Unknown":"keep"}"""
        );
        await File.WriteAllTextAsync(
            yamlPath,
            "$version: 1\nValue: 0\nLeft:\n  Value: 1\nRight:\n  Value: 2\nUnknown: keep\n"
        );

        await using (var json = CreateJsonContext<ParityDag256>(jsonPath))
        {
            await json.GetState<ParityDag256>().SaveAsync(settings => settings.Left!.Value = 10);
        }

        await using (var yaml = CreateYamlContext<ParityDag256>(yamlPath))
        {
            await yaml.GetState<ParityDag256>().SaveAsync(settings => settings.Left!.Value = 10);
        }

        var jsonWritten = await File.ReadAllTextAsync(jsonPath);
        var yamlWritten = await File.ReadAllTextAsync(yamlPath);
        jsonWritten.ShouldContain("\"Left\"");
        jsonWritten.Replace(" ", string.Empty).ShouldContain("\"Value\":10");
        jsonWritten.ShouldContain("\"Right\"");
        jsonWritten.ShouldContain("\"Unknown\"");
        yamlWritten.ShouldContain("Left:");
        yamlWritten.ShouldContain("Value: 10");
        yamlWritten.ShouldContain("Right:");
        yamlWritten.ShouldContain("Unknown");
    }

    [Test]
    public async Task Arrays_Sequences_Parity()
    {
        using var directory = new TemporaryDirectory();
        var jsonPath = Path.Combine(directory.FullPath, "scores.json");
        var yamlPath = Path.Combine(directory.FullPath, "scores.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(
            jsonPath,
            """{"$version":1,"Name":"base","Scores":[1,2],"Unknown":"keep"}"""
        );
        await File.WriteAllTextAsync(
            yamlPath,
            "$version: 1\nName: base\nScores:\n  - 1\n  - 2\nUnknown: keep\n"
        );

        await using (var json = CreateJsonContext<ParityRoot256>(jsonPath))
        {
            await json.GetState<ParityRoot256>()
                .SaveAsync(settings =>
                    settings.Scores = FragmentOperation<List<int>>.Set(new List<int> { 1, 2, 3 })
                );
        }

        await using (var yaml = CreateYamlContext<ParityRoot256>(yamlPath))
        {
            await yaml.GetState<ParityRoot256>()
                .SaveAsync(settings =>
                    settings.Scores = FragmentOperation<List<int>>.Set(new List<int> { 1, 2, 3 })
                );
        }

        (await File.ReadAllTextAsync(jsonPath)).ShouldContain("3");
        (await File.ReadAllTextAsync(yamlPath)).ShouldContain("- 3");

        await using (var json = CreateJsonContext<ParityRoot256>(jsonPath))
        {
            await json.GetState<ParityRoot256>()
                .SaveAsync(settings =>
                    settings.Scores = FragmentOperation<List<int>>.Set(new List<int> { 1 })
                );
        }

        await using (var yaml = CreateYamlContext<ParityRoot256>(yamlPath))
        {
            await yaml.GetState<ParityRoot256>()
                .SaveAsync(settings =>
                    settings.Scores = FragmentOperation<List<int>>.Set(new List<int> { 1 })
                );
        }

        var jsonWritten = await File.ReadAllTextAsync(jsonPath);
        var yamlWritten = await File.ReadAllTextAsync(yamlPath);
        jsonWritten.ShouldContain("\"Scores\"");
        jsonWritten.ShouldContain("\"Unknown\"");
        yamlWritten.ShouldContain("Scores:");
        yamlWritten.ShouldContain("Unknown");
    }

    [Test]
    public async Task Null_Default_Present_Parity()
    {
        using var directory = new TemporaryDirectory();
        var jsonPath = Path.Combine(directory.FullPath, "null.json");
        var yamlPath = Path.Combine(directory.FullPath, "null.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(
            jsonPath,
            """{"$version":1,"Name":"base","Optional":null,"Unknown":"keep"}"""
        );
        await File.WriteAllTextAsync(
            yamlPath,
            "$version: 1\nName: base\nOptional: null\nUnknown: keep\n"
        );

        await using (var json = CreateJsonContext<ParityRoot256>(jsonPath))
        {
            await json.GetState<ParityRoot256>()
                .SaveAsync(settings => settings.Optional = "present");
        }

        await using (var yaml = CreateYamlContext<ParityRoot256>(yamlPath))
        {
            await yaml.GetState<ParityRoot256>()
                .SaveAsync(settings => settings.Optional = "present");
        }

        (await File.ReadAllTextAsync(jsonPath)).ShouldContain("\"present\"");
        (await File.ReadAllTextAsync(yamlPath)).ShouldContain("present");
    }

    [Test]
    public async Task Unknown_Preservation_Parity()
    {
        using var directory = new TemporaryDirectory();
        var jsonPath = Path.Combine(directory.FullPath, "unknown.json");
        var yamlPath = Path.Combine(directory.FullPath, "unknown.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(
            jsonPath,
            """{"$version":1,"Name":"base","Child":{"Value":1,"Extra":"keep-nested"},"Unknown":{"Value":"keep"}}"""
        );
        await File.WriteAllTextAsync(
            yamlPath,
            "$version: 1\nName: base\nChild:\n  Value: 1\n  Extra: keep-nested\nUnknown:\n  Value: keep\n"
        );

        await using (var json = CreateJsonContext<ParityRoot256>(jsonPath))
        {
            await json.GetState<ParityRoot256>().SaveAsync(settings => settings.Count = 5);
        }

        await using (var yaml = CreateYamlContext<ParityRoot256>(yamlPath))
        {
            await yaml.GetState<ParityRoot256>().SaveAsync(settings => settings.Count = 5);
        }

        var jsonWritten = await File.ReadAllTextAsync(jsonPath);
        var yamlWritten = await File.ReadAllTextAsync(yamlPath);
        jsonWritten.ShouldContain("\"Extra\"");
        jsonWritten.ShouldContain("\"Unknown\"");
        yamlWritten.ShouldContain("Extra");
        yamlWritten.ShouldContain("Unknown");
    }

    [Test]
    public async Task Comment_Trivia_Parity()
    {
        using var directory = new TemporaryDirectory();
        var jsonPath = Path.Combine(directory.FullPath, "comment.json");
        var yamlPath = Path.Combine(directory.FullPath, "comment.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(
            jsonPath,
            "{\n  // Keep this comment.\n  \"$version\": 1,\n  \"Name\": \"base\" /* Keep inline. */\n}\n"
        );
        await File.WriteAllTextAsync(
            yamlPath,
            "$version: 1\nName: base # Keep inline.\n# Keep this comment.\n"
        );

        await using (var json = CreateJsonContext<ParityRoot256>(jsonPath))
        {
            await json.GetState<ParityRoot256>().SaveAsync(settings => settings.Count = 1);
        }

        await using (var yaml = CreateYamlContext<ParityRoot256>(yamlPath))
        {
            await yaml.GetState<ParityRoot256>().SaveAsync(settings => settings.Count = 1);
        }

        (await File.ReadAllTextAsync(jsonPath)).ShouldContain("Keep this comment.");
        (await File.ReadAllTextAsync(yamlPath)).ShouldContain("Keep this comment.");
    }

    private static ConfiglueContext CreateJsonContext<TModel>(string path)
        where TModel : IConfiglueFacadeModel<TModel> =>
        ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<TModel>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions { Path = path, WatchChanges = false }
                    )
                )
            );
        });

    private static ConfiglueContext CreateYamlContext<TModel>(string path)
        where TModel : IConfiglueFacadeModel<TModel> =>
        ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<TModel>(model =>
                model.Sources(sources =>
                    sources.FromYamlFile(
                        new YamlFileSourceOptions { Path = path, WatchChanges = false }
                    )
                )
            );
        });

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() =>
            FullPath = Path.Combine(
                Path.GetTempPath(),
                "Configlue.Tests",
                Guid.NewGuid().ToString("N")
            );

        public string FullPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(FullPath, recursive: true);
            }
            catch
            {
                // Best effort cleanup for test isolation.
            }
        }
    }
}
