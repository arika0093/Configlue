using System.Collections.Immutable;
using System.Text;
using Configlue.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Configlue.Generator.Compatibility.Tests;

public sealed class GeneratorHostCompatibilityTests
{
    private const string ModelSource = """
        using System.Collections.Generic;
        using Configlue;

        namespace UnityCompat.Sample
        {
            [ConfiglueModel("unity.sample", Version = 2)]
            [ConfigluePreviousVersion(typeof(PreviousSettings))]
            public partial class SampleSettings
            {
                [System.Text.Json.Serialization.JsonPropertyName("name")]
                public string Name { get; set; } = string.Empty;

                public int Count { get; set; }

                public Nested Poco { get; set; } = new Nested();

                public List<string> Items { get; set; } = new List<string>();

                public Dictionary<string, int> Map { get; set; } = new Dictionary<string, int>();
            }

            public class Nested
            {
                public bool Enabled { get; set; }

                public string? Label { get; set; }
            }

            [ConfiglueModel("unity.sample", Version = 1)]
            public partial class PreviousSettings
            {
                public string Name { get; set; } = string.Empty;
            }
        }
        """;

    [Test]
    [Arguments("class", true)]
    [Arguments("class", false)]
    [Arguments("struct", true)]
    [Arguments("struct", false)]
    [Arguments("record", true)]
    [Arguments("record", false)]
    public void GeneratedSource_FormatsNestedTypes_AndPreservesLiterals(
        string declaration,
        bool namespaced
    )
    {
        var source = $$"""
            using Configlue;
            {{(namespaced ? "namespace Formatting.Sample {" : "")}}
            [ConfiglueModel("format.{model}")]
            public partial {{declaration}} @event
            {
                public string @class { get; set; }
                public Nested Child { get; set; }
            }
            public class Nested
            {
                public int Value { get; set; }
            }
            {{(namespaced ? "}" : "")}}
            """;
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp9);
        var modelTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new ConfiglueGenerator().AsSourceGenerator() },
            parseOptions: parseOptions
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(modelTree),
            out var output,
            out var diagnostics
        );
        var exception = driver.GetRunResult().Results.Single().Exception;
        exception.ShouldBeNull(exception?.ToString());
        diagnostics.ShouldBeEmpty();
        var emit = output.Emit(Stream.Null);
        emit.Success.ShouldBeTrue(BuildDiagnosticMessage(emit.Diagnostics));

        var generated = GetGeneratedSource(output, modelTree);
        generated.ShouldContain("\"format.{model}\"");
        generated.ShouldNotContain("\t");
        generated.ShouldNotContain("\r");
        var modelIndent = namespaced ? "    " : "";
        generated.ShouldContain("\n" + modelIndent + "partial " + declaration + " @event :");
        generated.ShouldContain("\n" + modelIndent + "    public sealed class Fragment");
        generated.ShouldContain("\n" + modelIndent + "    public sealed class Details");
        generated.ShouldContain("\n" + modelIndent + "        public Details(");
        generated.ShouldContain(
            "\n" + modelIndent + "    public sealed class __ConfiglueStructural_"
        );
        foreach (var line in generated.Split('\n'))
        {
            line.ShouldBe(line.TrimEnd(), "Generated lines must not contain trailing whitespace.");
        }
    }

    [Test]
    public void Generator_EmitsMessagePackSupportOnRoslyn431Host()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp9);
        var modelTree = CSharpSyntaxTree.ParseText(ModelSource, parseOptions);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new ConfiglueGenerator().AsSourceGenerator() },
            parseOptions: parseOptions
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(modelTree),
            out var output,
            out var diagnostics
        );
        var exception = driver.GetRunResult().Results.Single().Exception;
        exception.ShouldBeNull(exception?.ToString());
        diagnostics.ShouldBeEmpty();
        GetGeneratedSource(output, modelTree).ShouldContain("FragmentMessagePackFormatter");
        var emit = output.Emit(Stream.Null);
        emit.Success.ShouldBeTrue(BuildDiagnosticMessage(emit.Diagnostics));
    }

    [Test]
    public void Generator_RunsOnRoslyn431Host_AndEmitsCSharp9Source()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp9);
        var modelTree = CSharpSyntaxTree.ParseText(ModelSource, parseOptions);
        var compilation = CreateCompilation(modelTree);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new ConfiglueGenerator().AsSourceGenerator() },
            parseOptions: parseOptions
        );

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics
        );

        var errors = generatorDiagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        (errors).ShouldBeEmpty(BuildDiagnosticMessage(errors));

        var generatedSource = GetGeneratedSource(outputCompilation, modelTree);
        (generatedSource).ShouldNotContain("ModuleInitializer");
        (generatedSource).ShouldContain("get; init;");
        (generatedSource).ShouldNotContain("class IsExternalInit");
        (generatedSource).ShouldNotContain("Assembly.Load");
        (generatedSource).ShouldNotContain("GetTypes(");
        (generatedSource).ShouldContain("namespace UnityCompat.Sample");
        (generatedSource).ShouldContain("namespace UnityCompat.Sample\n{");

        var emit = outputCompilation.Emit(Stream.Null);
        (emit.Success).ShouldBeTrue(BuildDiagnosticMessage(emit.Diagnostics));
    }

    [Test]
    public void ShouldEmitIsExternalInit_WhenCompilationLacksMarker()
    {
        var empty = CSharpCompilation.Create("ConfiglueGeneratorNoMarkerCompatibility");

        (ConfiglueGenerator.ShouldEmitIsExternalInit(empty, configured: true)).ShouldBeTrue();
        (ConfiglueGenerator.ShouldEmitIsExternalInit(empty, configured: false)).ShouldBeFalse();
    }

    [Test]
    public void ShouldEmitIsExternalInit_WhenCompilationProvidesMarker()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp9);
        var tree = CSharpSyntaxTree.ParseText("internal class Empty { }", parseOptions);
        var compilation = CreateCompilation(tree);

        (
            ConfiglueGenerator.ShouldEmitIsExternalInit(compilation, configured: true)
        ).ShouldBeFalse();
    }

    [Test]
    public void IsExternalInitSource_IsCSharp9Compatible()
    {
        var tree = CSharpSyntaxTree.ParseText(
            ConfiglueGenerator.IsExternalInitSource,
            new CSharpParseOptions(LanguageVersion.CSharp9)
        );

        (tree.GetDiagnostics()).ShouldBeEmpty();
        (ConfiglueGenerator.IsExternalInitSource).ShouldContain(
            "namespace System.Runtime.CompilerServices"
        );
        (ConfiglueGenerator.IsExternalInitSource).ShouldContain("class IsExternalInit");
    }

    private static CSharpCompilation CreateCompilation(SyntaxTree syntaxTree)
    {
        return CSharpCompilation.Create(
            "ConfiglueGeneratorCompatibility",
            [syntaxTree],
            BuildReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
    }

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trustedPlatformAssemblies =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        foreach (
            var path in trustedPlatformAssemblies.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            AddReference(builder, seen, path);
        }

        foreach (var path in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
        {
            AddReference(builder, seen, path);
        }

        return builder.ToImmutable();
    }

    private static void AddReference(
        ImmutableArray<MetadataReference>.Builder builder,
        HashSet<string> seen,
        string path
    )
    {
        if (!seen.Add(Path.GetFileNameWithoutExtension(path)))
        {
            return;
        }

        builder.Add(MetadataReference.CreateFromFile(path));
    }

    private static string GetGeneratedSource(Compilation outputCompilation, SyntaxTree modelTree)
    {
        var builder = new StringBuilder();
        foreach (var tree in outputCompilation.SyntaxTrees)
        {
            if (tree == modelTree)
            {
                continue;
            }

            builder.Append(tree.ToString()).Append('\n');
        }

        return builder.ToString();
    }

    private static string BuildDiagnosticMessage(IEnumerable<Diagnostic> diagnostics)
    {
        return string.Join(
            Environment.NewLine,
            diagnostics.Select(static diagnostic => diagnostic.ToString())
        );
    }
}
