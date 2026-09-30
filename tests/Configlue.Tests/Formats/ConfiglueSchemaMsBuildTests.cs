using System.Reflection;
using System.Text.Json.Nodes;
using Configlue.JsonSchema.MSBuild;
using Configlue.JsonSchema.MSBuild.Fixtures;

namespace Configlue.Tests;

public sealed class ConfiglueSchemaMsBuildTests
{
    private static string FixtureAssemblyPath => typeof(FixtureSettings).Assembly.Location;

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"configlue-msbuild-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Generate_DiscoversModelsAndPreservesNamingAndVersion()
    {
        var projectDirectory = CreateTempDirectory();
        var outputDirectory = CreateTempDirectory();
        try
        {
            var result = ConfiglueSchemaGenerator.Generate(
                new ConfiglueSchemaGenerationOptions
                {
                    AssemblyPath = FixtureAssemblyPath,
                    ProjectDirectory = projectDirectory,
                    OutputPath = outputDirectory,
                }
            );

            (result.Succeeded).ShouldBeTrue();
            result
                .Documents.Select(static document => document.FileName)
                .OrderBy(static name => name)
                .ShouldBe(["fixture.second.v1.json", "fixture.settings.v3.json"]);

            var settings = JsonNode.Parse(
                result.Documents.Single(document => document.ModelId == "fixture.settings").Content
            )!;
            (settings["$id"]!.GetValue<string>()).ShouldBe("fixture.settings.v3.json");
            var properties = settings["properties"]!;
            (properties["$version"]!["const"]!.GetValue<int>()).ShouldBe(3);
            (properties["MaxConnections"]!["maximum"]!.GetValue<decimal>()).ShouldBe(1000m);
            (properties["Name"]!["minLength"]!.GetValue<int>()).ShouldBe(3);
            (properties["Nested"]!["properties"]!["Code"]!["minLength"]!.GetValue<int>()).ShouldBe(
                2
            );
            (settings["additionalProperties"]!.GetValue<bool>()).ShouldBeFalse();
            (
                settings["required"]!
                    .AsArray()
                    .Select(static node => node!.GetValue<string>())
                    .ToArray()
            ).ShouldBe(["$version"]);
        }
        finally
        {
            DeleteDirectory(projectDirectory);
            DeleteDirectory(outputDirectory);
        }
    }

    [Test]
    public async Task Generate_SupportsDetailedLayout()
    {
        var projectDirectory = CreateTempDirectory();
        try
        {
            var result = ConfiglueSchemaGenerator.Generate(
                new ConfiglueSchemaGenerationOptions
                {
                    AssemblyPath = FixtureAssemblyPath,
                    ProjectDirectory = projectDirectory,
                    OutputPath = projectDirectory,
                    DocumentLayout = "Detailed",
                }
            );

            (result.Succeeded).ShouldBeTrue();
            var settings = JsonNode.Parse(
                result.Documents.Single(document => document.ModelId == "fixture.second").Content
            )!;
            var properties = settings["properties"]!;
            (properties["$configlue"]!["properties"]!["id"]!["const"]!.GetValue<string>()).ShouldBe(
                "fixture.second"
            );
            (properties["$value"]!["properties"]!["Email"]!["format"]!.GetValue<string>()).ShouldBe(
                "email"
            );
        }
        finally
        {
            DeleteDirectory(projectDirectory);
        }
    }

    [Test]
    public async Task Generate_AppliesSchemaBaseUri()
    {
        var projectDirectory = CreateTempDirectory();
        try
        {
            var result = ConfiglueSchemaGenerator.Generate(
                new ConfiglueSchemaGenerationOptions
                {
                    AssemblyPath = FixtureAssemblyPath,
                    ProjectDirectory = projectDirectory,
                    OutputPath = projectDirectory,
                    SchemaBaseUri = "https://example.test/schemas",
                }
            );

            (result.Succeeded).ShouldBeTrue();
            var settings = JsonNode.Parse(
                result.Documents.Single(document => document.ModelId == "fixture.settings").Content
            )!;
            (settings["$id"]!.GetValue<string>()).ShouldBe(
                "https://example.test/schemas/fixture.settings.v3.json"
            );
            (settings["properties"]!["$schema"]!["type"]!.GetValue<string>()).ShouldBe("string");
        }
        finally
        {
            DeleteDirectory(projectDirectory);
        }
    }

    [Test]
    public async Task Generate_ResolvesSolutionRootSchemasDirectory()
    {
        var solutionDirectory = CreateTempDirectory();
        var projectDirectory = Path.Combine(solutionDirectory, "src", "App");
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(Path.Combine(solutionDirectory, "MySolution.slnx"), "");
        try
        {
            var result = ConfiglueSchemaGenerator.Generate(
                new ConfiglueSchemaGenerationOptions
                {
                    AssemblyPath = FixtureAssemblyPath,
                    ProjectDirectory = projectDirectory,
                }
            );

            (result.Succeeded).ShouldBeTrue();
            (result.OutputDirectory!).ShouldBe(
                Path.GetFullPath(Path.Combine(solutionDirectory, "schemas"))
            );
        }
        finally
        {
            DeleteDirectory(solutionDirectory);
        }
    }

    [Test]
    public async Task Generate_FallsBackToProjectDirectorySchemas()
    {
        var projectDirectory = CreateTempDirectory();
        try
        {
            var result = ConfiglueSchemaGenerator.Generate(
                new ConfiglueSchemaGenerationOptions
                {
                    AssemblyPath = FixtureAssemblyPath,
                    ProjectDirectory = projectDirectory,
                }
            );

            (result.Succeeded).ShouldBeTrue();
            (result.OutputDirectory!).ShouldBe(
                Path.GetFullPath(Path.Combine(projectDirectory, "schemas"))
            );
        }
        finally
        {
            DeleteDirectory(projectDirectory);
        }
    }

    [Test]
    public async Task Generate_HonorsRelativeOutputOverride()
    {
        var projectDirectory = CreateTempDirectory();
        try
        {
            var result = ConfiglueSchemaGenerator.Generate(
                new ConfiglueSchemaGenerationOptions
                {
                    AssemblyPath = FixtureAssemblyPath,
                    ProjectDirectory = projectDirectory,
                    OutputPath = Path.Combine("custom", "schemas"),
                }
            );

            (result.Succeeded).ShouldBeTrue();
            (result.OutputDirectory!).ShouldBe(
                Path.GetFullPath(Path.Combine(projectDirectory, "custom", "schemas"))
            );
        }
        finally
        {
            DeleteDirectory(projectDirectory);
        }
    }

    [Test]
    public async Task Generate_DoesNotRewriteUnchangedSchemas()
    {
        var projectDirectory = CreateTempDirectory();
        var outputDirectory = CreateTempDirectory();
        try
        {
            var options = new ConfiglueSchemaGenerationOptions
            {
                AssemblyPath = FixtureAssemblyPath,
                ProjectDirectory = projectDirectory,
                OutputPath = outputDirectory,
            };

            var first = ConfiglueSchemaGenerator.Generate(options);
            (first.Succeeded).ShouldBeTrue();
            (first.WrittenFiles.Count).ShouldBe(first.Documents.Count);
            (first.UpToDateFiles).ShouldBeEmpty();

            var second = ConfiglueSchemaGenerator.Generate(options);
            (second.Succeeded).ShouldBeTrue();
            (second.WrittenFiles).ShouldBeEmpty();
            (second.UpToDateFiles.Count).ShouldBe(second.Documents.Count);
        }
        finally
        {
            DeleteDirectory(projectDirectory);
            DeleteDirectory(outputDirectory);
        }
    }

    [Test]
    public async Task Generate_ReportsInvalidDocumentLayout()
    {
        var projectDirectory = CreateTempDirectory();
        try
        {
            var result = ConfiglueSchemaGenerator.Generate(
                new ConfiglueSchemaGenerationOptions
                {
                    AssemblyPath = FixtureAssemblyPath,
                    ProjectDirectory = projectDirectory,
                    OutputPath = projectDirectory,
                    DocumentLayout = "Fancy",
                }
            );

            (result.Succeeded).ShouldBeFalse();
            (result.Diagnostics.Single().Code).ShouldBe("CWSC102");
        }
        finally
        {
            DeleteDirectory(projectDirectory);
        }
    }

    [Test]
    public void RuntimeGraph_DoesNotReferenceSchemaTooling()
    {
        var runtimeAssemblies = new[]
        {
            "Configlue",
            "Configlue.Abstraction",
            "Configlue.Core",
            "Configlue.Provider.Json",
        };
        foreach (var assemblyName in runtimeAssemblies)
        {
            var references = System
                .Reflection.Assembly.Load(assemblyName)
                .GetReferencedAssemblies()
                .Select(static reference => reference.Name)
                .ToArray();
            references.ShouldNotContain("JsonSchema.Net");
            references.ShouldNotContain("JsonSchema.Net.Generation");
            references.ShouldNotContain("JsonSchema.Net.Generation.DataAnnotations");
            references.ShouldNotContain("Configlue.JsonSchema.MSBuild");
        }
    }

    [Test]
    public async Task Generate_ReportsMissingAssembly()
    {
        var projectDirectory = CreateTempDirectory();
        try
        {
            var result = ConfiglueSchemaGenerator.Generate(
                new ConfiglueSchemaGenerationOptions
                {
                    AssemblyPath = Path.Combine(projectDirectory, "missing.dll"),
                    ProjectDirectory = projectDirectory,
                    OutputPath = projectDirectory,
                }
            );

            (result.Succeeded).ShouldBeFalse();
            (result.Diagnostics.Single().Code).ShouldBe("CWSC101");
        }
        finally
        {
            DeleteDirectory(projectDirectory);
        }
    }
}
