using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Configlue;
using Configlue.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Configlue.Tests.PublicApi;

public sealed class ConfiglueGeneratorDiagnosticTests
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    [Test]
    public async Task ValidAttribute_ProducesSchemaIdAndVersionFromPositionalId()
    {
        var result = RunGenerator(
            """
            using Configlue;

            namespace Sample;

            [ConfiglueModel("example.console-settings", Version = 1)]
            public partial class Settings
            {
                public int Value { get; set; }
            }
            """
        );

        (result.Diagnostics).ShouldBeEmpty();
        var generated = GetGeneratedSource(result);
        (generated).ShouldContain("\"example.console-settings\"");
        (generated).ShouldContain(", 1, ");
    }

    [Test]
    public async Task ValidAttribute_WithExplicitHigherVersion_EmitsConfiguredVersion()
    {
        var result = RunGenerator(
            """
            using Configlue;

            namespace Sample;

            [ConfiglueModel("example.console-settings", Version = 3)]
            public partial class Settings
            {
                public int Value { get; set; }
            }
            """
        );

        (result.Diagnostics).ShouldBeEmpty();
        (GetGeneratedSource(result)).ShouldContain(", 3, ");
    }

    [Test]
    public async Task ValidAttribute_WithoutVersion_DefaultsToInitialVersion()
    {
        var result = RunGenerator(
            """
            using Configlue;

            namespace Sample;

            [ConfiglueModel("example.console-settings")]
            public partial class Settings
            {
                public int Value { get; set; }
            }
            """
        );

        (result.Diagnostics).ShouldBeEmpty();
        (GetGeneratedSource(result)).ShouldContain(", 1, ");
    }

    [Test]
    public async Task BlankSchemaId_ReportsInvalidModelIdDiagnostic()
    {
        var result = RunGenerator(
            """
            using Configlue;

            namespace Sample;

            [ConfiglueModel("   ")]
            public partial class Settings
            {
                public int Value { get; set; }
            }
            """
        );

        (result.Diagnostics.Any(static diagnostic => diagnostic.Id == "CFG007")).ShouldBeTrue();
    }

    [Test]
    public async Task InvalidSchemaVersion_ReportsInvalidModelVersionDiagnostic()
    {
        var result = RunGenerator(
            """
            using Configlue;

            namespace Sample;

            [ConfiglueModel("example.console-settings", Version = 0)]
            public partial class Settings
            {
                public int Value { get; set; }
            }
            """
        );

        (result.Diagnostics.Any(static diagnostic => diagnostic.Id == "CFG008")).ShouldBeTrue();
    }

    [Test]
    public async Task PreviousVersionWithDifferentId_ReportsInvalidPreviousVersionDiagnostic()
    {
        var result = RunGenerator(
            """
            using Configlue;

            namespace Sample;

            [ConfiglueModel("current-settings", Version = 2)]
            [ConfigluePreviousVersion(typeof(OldSettings))]
            public partial class CurrentSettings
            {
                public int Value { get; set; }
            }

            [ConfiglueModel("renamed-settings", Version = 1)]
            public partial class OldSettings
            {
                public int Value { get; set; }
            }
            """
        );

        (result.Diagnostics.Any(static diagnostic => diagnostic.Id == "CFG006")).ShouldBeTrue();
    }

    [Test]
    public async Task OutputBoundary_CachesEquivalentOutputAndInvalidatesSemanticChanges()
    {
        const string source = """
                using Configlue;
            using System.Text.Json.Serialization;

            namespace Sample;

            [ConfiglueModel("example.cache-test")]
            public partial class Settings
            {
                [JsonPropertyName("old_value")]
                public int Value { get; set; }
            }
            """;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CreateCompilation(syntaxTree);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ConfiglueGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true
            )
        );

        driver = driver.RunGenerators(compilation);

        var triviaChangedTree = CSharpSyntaxTree.ParseText(
            source.Replace("public int Value", "public  int Value", StringComparison.Ordinal),
            parseOptions
        );
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(syntaxTree, triviaChangedTree));
        var equivalentOutput = driver.GetRunResult();
        (GetStepReason(equivalentOutput, "ConfiglueGenerator.Analysis")).ShouldBe(
            IncrementalStepRunReason.Unchanged
        );
        (GetStepReason(equivalentOutput, "ConfiglueGenerator.Output")).ShouldBe(
            IncrementalStepRunReason.Cached
        );

        var semanticChangedTree = CSharpSyntaxTree.ParseText(
            source
                .Replace("int Value", "string Value", StringComparison.Ordinal)
                .Replace("old_value", "new_value", StringComparison.Ordinal),
            parseOptions
        );
        var semanticChangedCompilation = compilation.ReplaceSyntaxTree(
            syntaxTree,
            semanticChangedTree
        );
        driver = driver.RunGenerators(semanticChangedCompilation);
        var changedOutput = driver.GetRunResult();
        (GetStepReason(changedOutput, "ConfiglueGenerator.Analysis")).ShouldBe(
            IncrementalStepRunReason.Modified
        );
        (GetStepReason(changedOutput, "ConfiglueGenerator.Output")).ShouldBe(
            IncrementalStepRunReason.Modified
        );
        (GetGeneratedSource(changedOutput)).ShouldContain("ConfigValueDetails<string>");
        (GetGeneratedSource(changedOutput)).ShouldContain("\"new_value\"");
    }

    private static GeneratorDriverRunResult RunGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview)
        );
        var compilation = CreateCompilation(syntaxTree);
        var driver = CSharpGeneratorDriver.Create(new ConfiglueGenerator().AsSourceGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static CSharpCompilation CreateCompilation(SyntaxTree syntaxTree)
    {
        return CSharpCompilation.Create(
            "ConfiglueGeneratorDiagnosticTests",
            [syntaxTree],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
    }

    private static IncrementalStepRunReason GetStepReason(
        GeneratorDriverRunResult result,
        string trackingName
    )
    {
        return result.Results.Single().TrackedSteps[trackingName].Single().Outputs.Single().Reason;
    }

    private static string GetGeneratedSource(GeneratorDriverRunResult result)
    {
        var builder = new StringBuilder();
        foreach (var generatorResult in result.Results)
        {
            foreach (var generated in generatorResult.GeneratedSources)
            {
                builder.AppendLine(generated.SourceText.ToString());
            }
        }

        return builder.ToString();
    }

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddReference(builder, seen, typeof(object).Assembly);
        AddReference(builder, seen, typeof(ConfiglueModelAttribute).Assembly);
        var trustedPlatformAssemblies =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        foreach (
            var path in trustedPlatformAssemblies.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            if (seen.Add(Path.GetFileNameWithoutExtension(path)))
            {
                builder.Add(MetadataReference.CreateFromFile(path));
            }
        }

        return builder.ToImmutable();
    }

    private static void AddReference(
        ImmutableArray<MetadataReference>.Builder builder,
        HashSet<string> seen,
        Assembly assembly
    )
    {
        if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
        {
            return;
        }

        if (seen.Add(assembly.GetName().Name ?? assembly.Location))
        {
            builder.Add(MetadataReference.CreateFromFile(assembly.Location));
        }
    }
}
