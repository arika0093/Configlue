using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Configlue.JsonSchema.MSBuild;
using Configlue.JsonSchema.MSBuild.Fixtures;
using Microsoft.Build.Framework;

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

    [Test]
    public async Task Generate_IsRaceSafeWhenRunsShareAnOutputDirectory()
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

            var results = await Task.WhenAll(
                Enumerable
                    .Range(0, 8)
                    .Select(_ =>
                        Task.Run(() => ConfiglueSchemaGenerator.Generate(options))
                    )
            );

            (results.All(static result => result.Succeeded)).ShouldBeTrue(
                string.Join(
                    " | ",
                    results
                        .SelectMany(static result => result.Diagnostics)
                        .Select(static diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")
                )
            );
            (results.SelectMany(static result => result.Diagnostics)).ShouldBeEmpty();
            foreach (var file in Directory.GetFiles(outputDirectory, "*.json"))
            {
                (JsonNode.Parse(await File.ReadAllTextAsync(file))).ShouldNotBeNull();
            }
        }
        finally
        {
            DeleteDirectory(projectDirectory);
            DeleteDirectory(outputDirectory);
        }
    }

    [Test]
    public void DependencyPolicy_ApprovedVersionsMatchReferencesAndLockedGraph()
    {
        var toolingDirectory = Path.Combine(
            RepositoryRoot,
            "src",
            "tools",
            "Configlue.JsonSchema.MSBuild"
        );
        var root = XDocument.Load(Path.Combine(toolingDirectory, "DependencyPolicy.props")).Root!;
        string Property(string name) => root.Descendants(name).Single().Value;

        (Property("JsonSchemaNetVersion")).ShouldBe(Property("AllowedJsonSchemaNetVersion"));
        (Property("JsonSchemaNetGenerationVersion")).ShouldBe(
            Property("AllowedJsonSchemaNetGenerationVersion")
        );
        (Property("JsonSchemaNetDataAnnotationsVersion")).ShouldBe(
            Property("AllowedJsonSchemaNetDataAnnotationsVersion")
        );

        using var lockDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(toolingDirectory, "packages.lock.json"))
        );
        var locked = lockDocument
            .RootElement.GetProperty("dependencies")
            .GetProperty("net10.0");

        var propertyValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (
            var property in root
                .Descendants()
                .Where(static element => element.Parent?.Name.LocalName == "PropertyGroup")
        )
        {
            propertyValues[property.Name.LocalName] = property.Value;
        }

        string Expand(string value) =>
            value.StartsWith("$(", StringComparison.Ordinal) && value.EndsWith(')')
                ? propertyValues[value[2..^1]]
                : value;

        var approved = root
            .Descendants("ConfiglueJsonSchemaApprovedDependency")
            .ToDictionary(
                static item => item.Attribute("Include")!.Value,
                item => Expand(item.Attribute("Version")!.Value)
            );
        (approved.Count).ShouldBeGreaterThan(0);
        foreach (var (id, version) in approved)
        {
            (locked.TryGetProperty(id, out var entry)).ShouldBeTrue(
                $"The approved dependency '{id}' is missing from packages.lock.json."
            );
            (entry.GetProperty("resolved").GetString()).ShouldBe(version);
        }
    }

    [Test]
    public async Task SchemaGenerationTarget_RunsOnceForTheFirstTargetFramework()
    {
        var directory = CreateTempDirectory();
        try
        {
            var firstFramework = WriteMsBuildHarness(directory, "netstandard2.0", extraProperties: null);
            var secondFramework = WriteMsBuildHarness(directory, "net10.0", extraProperties: null);

            var results = await Task.WhenAll(
                EvaluateMsBuildPropertyAsync(firstFramework, "_ConfiglueSchemaShouldRun"),
                EvaluateMsBuildPropertyAsync(secondFramework, "_ConfiglueSchemaShouldRun")
            );

            (results[0]).ShouldBe("true");
            (results[1]).ShouldBeEmpty();
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Test]
    public async Task SchemaGenerationTarget_CanBeOptedOutOrTargetedExplicitly()
    {
        var directory = CreateTempDirectory();
        try
        {
            var optedOut = WriteMsBuildHarness(
                directory,
                "netstandard2.0",
                "<ConfiglueGenerateSchemas>false</ConfiglueGenerateSchemas>"
            );
            var explicitTarget = WriteMsBuildHarness(
                directory,
                "net10.0",
                "<ConfiglueSchemaTargetFramework>net10.0</ConfiglueSchemaTargetFramework>"
            );
            var nonTarget = WriteMsBuildHarness(
                directory,
                "netstandard2.0",
                "<ConfiglueSchemaTargetFramework>net10.0</ConfiglueSchemaTargetFramework>"
            );

            var results = await Task.WhenAll(
                EvaluateMsBuildPropertyAsync(optedOut, "_ConfiglueSchemaShouldRun"),
                EvaluateMsBuildPropertyAsync(explicitTarget, "_ConfiglueSchemaShouldRun"),
                EvaluateMsBuildPropertyAsync(nonTarget, "_ConfiglueSchemaShouldRun")
            );

            (results[0]).ShouldBeEmpty();
            (results[1]).ShouldBe("true");
            (results[2]).ShouldBeEmpty();
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Test]
    public async Task Generate_FailsWhenModelDependencyIsMissing()
    {
        var fixtureRoot = CreateTempDirectory();
        var outputDirectory = CreateTempDirectory();
        try
        {
            var modelsAssembly = await BuildTypeLoadFixtureAsync(fixtureRoot);
            var missingDependency = Path.Combine(
                Path.GetDirectoryName(modelsAssembly)!,
                "TypeLoadFixture.Base.dll"
            );
            if (File.Exists(missingDependency))
            {
                File.Delete(missingDependency);
            }

            var result = ConfiglueSchemaGenerator.Generate(
                new ConfiglueSchemaGenerationOptions
                {
                    AssemblyPath = modelsAssembly,
                    ProjectDirectory = fixtureRoot,
                    OutputPath = outputDirectory,
                }
            );

            (result.Succeeded).ShouldBeFalse();
            (result.Diagnostics.Any(static diagnostic => diagnostic.Code == "CWSC108")).ShouldBeTrue(
                string.Join(
                    " | ",
                    result.Diagnostics.Select(static diagnostic =>
                        $"{diagnostic.Code}:{diagnostic.Message}"
                    )
                )
            );
            result
                .Diagnostics.Single(static diagnostic => diagnostic.Code == "CWSC108")
                .Message.ShouldContain("TypeLoadFixture.Base");
            (result.WrittenFiles).ShouldBeEmpty();
            (result.Documents).ShouldBeEmpty();
            (Directory.GetFiles(outputDirectory, "*.json")).ShouldBeEmpty();

            var buildEngine = new RecordingBuildEngine();
            var task = new GenerateConfiglueSchemas
            {
                BuildEngine = buildEngine,
                AssemblyPath = modelsAssembly,
                ProjectDirectory = fixtureRoot,
                OutputPath = outputDirectory,
            };
            (task.Execute()).ShouldBeFalse();
            (task.WrittenFiles).ShouldBeEmpty();
            (buildEngine.Errors.Any(static message => message.Contains("CWSC108"))).ShouldBeTrue(
                string.Join(" | ", buildEngine.Errors)
            );
        }
        finally
        {
            DeleteDirectory(fixtureRoot);
            DeleteDirectory(outputDirectory);
        }
    }

    [Test]
    public async Task Generate_SucceedsWhenAllModelDependenciesArePresent()
    {
        var fixtureRoot = CreateTempDirectory();
        var outputDirectory = CreateTempDirectory();
        try
        {
            var modelsAssembly = await BuildTypeLoadFixtureAsync(fixtureRoot);

            var result = ConfiglueSchemaGenerator.Generate(
                new ConfiglueSchemaGenerationOptions
                {
                    AssemblyPath = modelsAssembly,
                    ProjectDirectory = fixtureRoot,
                    OutputPath = outputDirectory,
                }
            );

            (result.Succeeded).ShouldBeTrue(
                string.Join(
                    " | ",
                    result.Diagnostics.Select(static diagnostic =>
                        $"{diagnostic.Code}:{diagnostic.Message}"
                    )
                )
            );
            (
                result
                    .Documents.Select(static document => document.FileName)
                    .OrderBy(static name => name)
            ).ShouldBe(["typeload.bad.v1.json", "typeload.good.v1.json"]);
            (result.WrittenFiles.Count).ShouldBe(2);
        }
        finally
        {
            DeleteDirectory(fixtureRoot);
            DeleteDirectory(outputDirectory);
        }
    }

    private static async Task<string> BuildTypeLoadFixtureAsync(string root)
    {
        var baseDirectory = Path.Combine(root, "base");
        var modelsDirectory = Path.Combine(root, "models");
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(modelsDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(baseDirectory, "Base.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>TypeLoadFixture.Base</AssemblyName>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """
        );
        await File.WriteAllTextAsync(
            Path.Combine(baseDirectory, "MissingBase.cs"),
            """
            namespace TypeLoadFixture.Base;
            public class MissingBase
            {
                public string? Value { get; set; }
            }
            """
        );

        var abstractionPath = Path.Combine(
            RepositoryRoot,
            "src",
            "basic",
            "Configlue.Abstraction",
            "Configlue.Abstraction.csproj"
        );
        await File.WriteAllTextAsync(
            Path.Combine(modelsDirectory, "Models.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>TypeLoadFixture.Models</AssemblyName>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{Path.Combine(baseDirectory, "Base.csproj")}" />
                <ProjectReference Include="{abstractionPath}" />
              </ItemGroup>
            </Project>
            """
        );
        await File.WriteAllTextAsync(
            Path.Combine(modelsDirectory, "Models.cs"),
            """
            using Configlue;
            using TypeLoadFixture.Base;
            namespace TypeLoadFixture.Models;
            [ConfiglueModel("typeload.good")]
            public sealed class GoodModel
            {
                public string? Name { get; set; }
            }
            [ConfiglueModel("typeload.bad")]
            public sealed class BadModel : MissingBase
            {
                public string? Extra { get; set; }
            }
            """
        );

        await RunDotNetBuildAsync(Path.Combine(modelsDirectory, "Models.csproj"));

        var modelsAssembly = Path.Combine(
            modelsDirectory,
            "bin",
            "Debug",
            "net10.0",
            "TypeLoadFixture.Models.dll"
        );
        if (!File.Exists(modelsAssembly))
        {
            throw new InvalidOperationException(
                $"The type-load fixture assembly was not built: {modelsAssembly}"
            );
        }

        return modelsAssembly;
    }

    private static async Task RunDotNetBuildAsync(string projectPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Debug");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("q");

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("The dotnet CLI could not be started.");
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet build exited with code {process.ExitCode}: {standardOutput} {standardError}"
            );
        }
    }

    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Configlue.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "The repository root (containing Configlue.slnx) could not be located."
        );
    }

    private static string WriteMsBuildHarness(
        string directory,
        string targetFramework,
        string? extraProperties
    )
    {
        var buildDirectory = Path.Combine(
            RepositoryRoot,
            "src",
            "tools",
            "Configlue.JsonSchema.MSBuild",
            "build"
        );
        var content = $"""
            <Project>
              <PropertyGroup>
                <TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>
                <TargetFramework>{targetFramework}</TargetFramework>
                {extraProperties}
              </PropertyGroup>
              <Import Project="{Path.Combine(buildDirectory, "Configlue.JsonSchema.MSBuild.props")}" />
              <Import Project="{Path.Combine(buildDirectory, "Configlue.JsonSchema.MSBuild.targets")}" />
            </Project>
            """;
        var path = Path.Combine(directory, $"harness-{Guid.NewGuid():N}.proj");
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Messages { get; } = [];

        public bool ContinueOnError => false;

        public string ProjectFileOfTaskNode => string.Empty;

        public int LineNumberOfTaskNode => 0;

        public int ColumnNumberOfTaskNode => 0;

        public bool BuildProjectFile(
            string projectFileName,
            string[] targetNames,
            IDictionary globalProperties,
            IDictionary targetOutputs
        ) => false;

        public void LogCustomEvent(CustomBuildEventArgs e) { }

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? string.Empty);

        public void LogMessageEvent(BuildMessageEventArgs e) =>
            Messages.Add(e.Message ?? string.Empty);

        public void LogWarningEvent(BuildWarningEventArgs e) =>
            Warnings.Add(e.Message ?? string.Empty);
    }

    private static async Task<string> EvaluateMsBuildPropertyAsync(
        string projectPath,
        string propertyName
    )
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add($"-getProperty:{propertyName}");
        startInfo.ArgumentList.Add("-nologo");

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("The dotnet CLI could not be started.");
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet msbuild exited with code {process.ExitCode}: {standardError}"
            );
        }

        return standardOutput.Trim();
    }
}
