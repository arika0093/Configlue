using Configlue;
using Configlue.CompilerServices;
using Configlue.Provider.Json;
using Configlue.Provider.Yaml;

namespace Configlue.Tests;

[ConfiglueModel("example.recursive-node-206", Version = 1)]
public partial class RecursiveNode206
{
    public int Value { get; set; }

    public RecursiveNode206? Next { get; set; }
}

[ConfiglueModel("example.mutual-a-206", Version = 1)]
public partial class MutualA206
{
    public int Value { get; set; }

    public MutualB206? B { get; set; }
}

[ConfiglueModel("example.mutual-b-206", Version = 1)]
public partial class MutualB206
{
    public int Value { get; set; }

    public MutualA206? A { get; set; }
}

[ConfiglueModel("example.shared-leaf-206", Version = 1)]
public partial class SharedLeaf206
{
    public int Value { get; set; }
}

[ConfiglueModel("example.dag-root-206", Version = 1)]
public partial class DagRoot206
{
    public int Value { get; set; }

    public SharedLeaf206? Left { get; set; }

    public SharedLeaf206? Right { get; set; }
}

public sealed class RecursiveSchemaShapeTests
{
    [Test]
    public async Task Json_SelfReference_Null_Registers()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "node.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"$version":1,"Value":1,"Next":null}""");

        await using var context = CreateJsonContext<RecursiveNode206>(path);
        var value = await context.GetState<RecursiveNode206>().GetValueAsync();
        value.Value.ShouldBe(1);
        value.Next.ShouldBeNull();
    }

    [Test]
    public async Task Yaml_SelfReference_Null_Registers()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "node.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "$version: 1\nValue: 1\nNext: null\n");

        await using var context = CreateYamlContext<RecursiveNode206>(path);
        var value = await context.GetState<RecursiveNode206>().GetValueAsync();
        value.Value.ShouldBe(1);
        value.Next.ShouldBeNull();
    }

    [Test]
    public async Task Json_SelfReference_TwoLevels_Reads()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "node.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """{"$version":1,"Value":1,"Next":{"Value":2,"Next":null}}"""
        );

        await using var context = CreateJsonContext<RecursiveNode206>(path);
        var value = await context.GetState<RecursiveNode206>().GetValueAsync();
        value.Value.ShouldBe(1);
        value.Next.ShouldNotBeNull();
        value.Next!.Value.ShouldBe(2);
        value.Next.Next.ShouldBeNull();
    }

    [Test]
    public async Task Yaml_SelfReference_TwoLevels_Reads()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "node.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            "$version: 1\nValue: 1\nNext:\n  Value: 2\n  Next: null\n"
        );

        await using var context = CreateYamlContext<RecursiveNode206>(path);
        var value = await context.GetState<RecursiveNode206>().GetValueAsync();
        value.Value.ShouldBe(1);
        value.Next.ShouldNotBeNull();
        value.Next!.Value.ShouldBe(2);
        value.Next.Next.ShouldBeNull();
    }

    [Test]
    public async Task Json_SelfReference_ThreeLevels_Reads()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "node.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """{"$version":1,"Value":1,"Next":{"Value":2,"Next":{"Value":3,"Next":null}}}"""
        );

        await using var context = CreateJsonContext<RecursiveNode206>(path);
        var value = await context.GetState<RecursiveNode206>().GetValueAsync();
        value.Value.ShouldBe(1);
        value.Next!.Value.ShouldBe(2);
        value.Next.Next!.Value.ShouldBe(3);
        value.Next.Next.Next.ShouldBeNull();
    }

    [Test]
    public async Task Yaml_SelfReference_ThreeLevels_Reads()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "node.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            "$version: 1\nValue: 1\nNext:\n  Value: 2\n  Next:\n    Value: 3\n    Next: null\n"
        );

        await using var context = CreateYamlContext<RecursiveNode206>(path);
        var value = await context.GetState<RecursiveNode206>().GetValueAsync();
        value.Value.ShouldBe(1);
        value.Next!.Value.ShouldBe(2);
        value.Next.Next!.Value.ShouldBe(3);
        value.Next.Next.Next.ShouldBeNull();
    }

    [Test]
    public async Task Json_MutualReference_Reads()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "mutual.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"$version":1,"Value":1,"B":{"Value":2,"A":null}}""");

        await using var context = CreateJsonContext<MutualA206>(path);
        var value = await context.GetState<MutualA206>().GetValueAsync();
        value.Value.ShouldBe(1);
        value.B.ShouldNotBeNull();
        value.B!.Value.ShouldBe(2);
        value.B.A.ShouldBeNull();
    }

    [Test]
    public async Task Yaml_MutualReference_Reads()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "mutual.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "$version: 1\nValue: 1\nB:\n  Value: 2\n  A: null\n");

        await using var context = CreateYamlContext<MutualA206>(path);
        var value = await context.GetState<MutualA206>().GetValueAsync();
        value.Value.ShouldBe(1);
        value.B.ShouldNotBeNull();
        value.B!.Value.ShouldBe(2);
        value.B.A.ShouldBeNull();
    }

    [Test]
    public async Task Json_SharedDag_Reads()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "dag.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """{"$version":1,"Value":0,"Left":{"Value":1},"Right":{"Value":2}}"""
        );

        await using var context = CreateJsonContext<DagRoot206>(path);
        var value = await context.GetState<DagRoot206>().GetValueAsync();
        value.Left.ShouldNotBeNull();
        value.Left!.Value.ShouldBe(1);
        value.Right.ShouldNotBeNull();
        value.Right!.Value.ShouldBe(2);
    }

    [Test]
    public async Task Yaml_SharedDag_Reads()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "dag.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            "$version: 1\nValue: 0\nLeft:\n  Value: 1\nRight:\n  Value: 2\n"
        );

        await using var context = CreateYamlContext<DagRoot206>(path);
        var value = await context.GetState<DagRoot206>().GetValueAsync();
        value.Left.ShouldNotBeNull();
        value.Left!.Value.ShouldBe(1);
        value.Right.ShouldNotBeNull();
        value.Right!.Value.ShouldBe(2);
    }

    [Test]
    public async Task Json_Section_ReadsRecursive()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "section.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """{"App":{"Settings":{"$version":1,"Value":5,"Next":{"Value":6,"Next":null}}}}"""
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<RecursiveNode206>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Path = path,
                            WatchChanges = false,
                            SectionPath = "App:Settings",
                        }
                    )
                )
            );
        });

        var value = await context.GetState<RecursiveNode206>().GetValueAsync();
        value.Value.ShouldBe(5);
        value.Next!.Value.ShouldBe(6);
    }

    [Test]
    public async Task Yaml_Section_ReadsRecursive()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "section.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            "App:\n  Settings:\n    $version: 1\n    Value: 5\n    Next:\n      Value: 6\n      Next: null\n"
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<RecursiveNode206>(model =>
                model.Sources(sources =>
                    sources.FromYamlFile(
                        new YamlFileSourceOptions
                        {
                            Path = path,
                            WatchChanges = false,
                            SectionPath = "App:Settings",
                        }
                    )
                )
            );
        });

        var value = await context.GetState<RecursiveNode206>().GetValueAsync();
        value.Value.ShouldBe(5);
        value.Next!.Value.ShouldBe(6);
    }

    [Test]
    public async Task Json_DeepUnknown_PreservedOnWrite()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "node.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """
            {
              // Root comment.
              "$version": 1,
              "Value": 1,
              "Next": {
                "Value": 2,
                "Next": null,
                "DeepUnknown": { "Kept": true } // Inline comment.
              },
              "Unknown": { "Value": "keep" }
            }
            """
        );

        await using var context = CreateJsonContext<RecursiveNode206>(path);
        var state = context.GetState<RecursiveNode206>();
        await state.SaveAsync(settings => settings.Value = 10);

        var written = await File.ReadAllTextAsync(path);
        written.ShouldContain("\"Value\": 10");
        written.ShouldContain("\"DeepUnknown\"");
        written.ShouldContain("\"Unknown\"");
        written.ShouldContain("Root comment.");
    }

    [Test]
    public async Task Yaml_DeepUnknown_PreservedOnWrite()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "node.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            "$version: 1\nValue: 1\n# Root comment.\nNext:\n  Value: 2\n  Next: null\n  DeepUnknown:\n    Kept: true\nUnknown:\n  Value: keep\n"
        );

        await using var context = CreateYamlContext<RecursiveNode206>(path);
        var state = context.GetState<RecursiveNode206>();
        await state.SaveAsync(settings => settings.Value = 10);

        var written = await File.ReadAllTextAsync(path);
        written.ShouldContain("DeepUnknown");
        written.ShouldContain("Unknown");
        written.ShouldContain("Root comment.");
    }

    [Test]
    public async Task Json_Unset_RemovesOwnedKeepsUnknown()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "node.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """{"$version":1,"Value":1,"Next":{"Value":2,"Next":null},"Unknown":"keep"}"""
        );

        await using var context = CreateJsonContext<RecursiveNode206>(path);
        var state = context.GetState<RecursiveNode206>();
        await state.SaveAsync(patch => patch.Next.Unset());

        var written = await File.ReadAllTextAsync(path);
        written.ShouldNotContain("\"Next\"");
        written.ShouldContain("\"Unknown\"");
        written.ShouldContain("\"Value\"");
    }

    [Test]
    public async Task Yaml_Unset_RemovesOwnedKeepsUnknown()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "node.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            "$version: 1\nValue: 1\nNext:\n  Value: 2\n  Next: null\nUnknown: keep\n"
        );

        await using var context = CreateYamlContext<RecursiveNode206>(path);
        var state = context.GetState<RecursiveNode206>();
        await state.SaveAsync(patch => patch.Next.Unset());

        var written = await File.ReadAllTextAsync(path);
        written.ShouldNotContain("Next:");
        written.ShouldContain("Unknown");
        written.ShouldContain("Value:");
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
