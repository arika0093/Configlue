using System.Collections.Immutable;
using System.Text;
using Configlue.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Configlue.Generator.Compatibility.Tests;

// Minimal host-contract proof for the supported compiler/runtime matrix
// (see docs/compat-matrix.md). Unity 6 pins the Roslyn 4.3 host and the C# 9
// language ceiling, so these tests prove: the 4.3.1-built generators run on a
// C# 9 host, emitted code compiles as C# 9, and the IsExternalInit shim maps to
// compilations lacking the BCL marker. Exhaustive diagnostic/behavior matrices
// are intentionally not here; runtime behavior stays covered in
// SparseFragments.Tests and Configlue.Tests contracts.
public sealed class GeneratorHostCompatibilityTests
{
    [Test]
    public void StandaloneTypedMutationCompilesWithCSharp9Host()
    {
        const string source = """
            using SparseFragments;
            [SparseFragmentModel]
            public partial class Settings
            {
                public Child Child { get; set; } = new Child();
                public int Count { get; set; }
            }
            public partial class Child { public int Value { get; set; } }
            public static class Consumer
            {
                public static Settings.Fragment Edit(Settings.Fragment input)
                {
                    var patch = new Settings.Patch { Count = FragmentOperation<int>.Unset };
                    patch.Child.Value = 7;
                    var builder = input.Apply(patch).ToBuilder();
                    builder.Count = 9;
                    return builder.Build();
                }
            }
            """;
        var options = new CSharpParseOptions(LanguageVersion.CSharp9);
        var tree = CSharpSyntaxTree.ParseText(source, options);
        var driver = CSharpGeneratorDriver.Create(
            new[] { new SparseFragments.Generator.SparseFragmentsGenerator().AsSourceGenerator() },
            parseOptions: options
        );
        driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(tree),
            out var output,
            out var diagnostics
        );
        diagnostics.ShouldBeEmpty();
        var emit = output.Emit(Stream.Null);
        emit.Success.ShouldBeTrue(BuildDiagnosticMessage(emit.Diagnostics));
    }

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
    public void RequiredMember_IsUnderstoodOnRoslyn431Host()
    {
        // Minimal 4.3.1-host proof for RoslynSymbolCompat.IsRequired
        // (see docs/compat-matrix.md §2-§3): the 4.3.1-built generator must
        // understand a required member authored on a modern SDK without a
        // newer host. Roslyn 4.3.1 exposes C# 11 required members through its
        // preview parser, so Preview here is still the old host.
        const string source = """
            using Configlue;
            [ConfiglueModel("required-host-proof")]
            public partial class RequiredHostSettings
            {
                public required int Identity { get; init; }
                public string Name { get; set; } = string.Empty;
            }
            """;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
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
        diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty(BuildDiagnosticMessage(diagnostics));
        var generatedSource = GetGeneratedSource(output, modelTree);
        generatedSource.ShouldContain("Identity");
        var emit = output.Emit(Stream.Null);
        emit.Success.ShouldBeTrue(BuildDiagnosticMessage(emit.Diagnostics));
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
