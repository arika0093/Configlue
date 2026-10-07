using System.Collections.Immutable;
using Configlue.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Configlue.Generator.Compatibility.Tests;

public sealed class PromotedFragmentCompatibilityTests
{
    private static CSharpCompilation CreateCompilation(params SyntaxTree[] trees)
    {
        return CSharpCompilation.Create(
            "PromotedCompatibility",
            trees,
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

    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics, string Generated) RunSparse(
        string source
    )
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new SparseFragments.Generator.SparseFragmentsGenerator().AsSourceGenerator()]
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(tree),
            out var output,
            out var diagnostics
        );
        var exception = driver.GetRunResult().Results.Single().Exception;
        exception.ShouldBeNull(exception?.ToString());
        var generated = GetGeneratedSource(output, tree);
        return (output, diagnostics, generated);
    }

    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics, string Generated, GeneratorDriver Driver) RunConfiglue(
        string source
    )
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ConfiglueGenerator().AsSourceGenerator()]
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(tree),
            out var output,
            out var diagnostics
        );
        var exception = driver.GetRunResult().Results.Single().Exception;
        exception.ShouldBeNull(exception?.ToString());
        var generated = GetGeneratedSource(output, tree);
        return (output, diagnostics, generated, driver);
    }

    private static string GetGeneratedSource(Compilation outputCompilation, SyntaxTree modelTree)
    {
        var builder = new System.Text.StringBuilder();
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

    [Test]
    public void Sparse_PartialChild_GetsFragmentPatchAndParentUsesIt()
    {
        const string source = """
            using SparseFragments;
            [SparseFragmentModel]
            public partial class Settings
            {
                public Child Child { get; set; } = new();
            }
            public partial class Child
            {
                public string? Host { get; set; }
                public int Port { get; set; }
            }
            """;
        var (output, diagnostics, generated) = RunSparse(source);
        diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        generated.ShouldContain("partial class Child");
        generated.ShouldContain("class Fragment");
        generated.ShouldContain("class Patch");
        generated.ShouldContain("class FragmentBuilder");
        generated.ShouldContain("Child.Fragment");
        generated.ShouldNotContain("__SparseStructural_");
        output.Emit(Stream.Null).Success.ShouldBeTrue();
    }

    [Test]
    public void Sparse_NonPartialChild_RequiresExplicitPolicy()
    {
        // Intentional product-policy divergence (#340): the SparseFragments
        // generator selects atomic handling for non-partial POCOs and reports
        // SPF007, while the Configlue generator keeps them structural via shared
        // policy (see Configlue_NonPartial_StaysStructural below). This parity
        // test owns that difference.
        const string source = """
            using SparseFragments;
            [SparseFragmentModel]
            public partial class Settings
            {
                public Holder Holder { get; set; } = new();
            }
            public partial class Holder
            {
                public NonPartialLeaf Leaf { get; set; } = new();
            }
            public class NonPartialLeaf
            {
                public string? Value { get; set; }
            }
            """;
        var (_, diagnostics, _) = RunSparse(source);
        diagnostics
            .Any(static d => d.Id == "SPF007" && d.Severity == DiagnosticSeverity.Error)
            .ShouldBeTrue(BuildDiagnosticMessage(diagnostics));
    }

    [Test]
    public void Sparse_RecursivePartialDescendants_GetApis()
    {
        const string source = """
            using SparseFragments;
            [SparseFragmentModel]
            public partial class Root
            {
                public Mid Mid { get; set; } = new();
            }
            public partial class Mid
            {
                public LeafNode Inner { get; set; } = new();
            }
            public partial class LeafNode
            {
                public string? Name { get; set; }
            }
            """;
        var (output, diagnostics, generated) = RunSparse(source);
        diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        generated.ShouldContain("partial class Mid");
        generated.ShouldContain("partial class LeafNode");
        generated.ShouldContain("Mid.Fragment");
        generated.ShouldContain("LeafNode.Fragment");
        output.Emit(Stream.Null).Success.ShouldBeTrue();
    }

    [Test]
    public void Sparse_MultipleRoots_ShareOneChildSurface()
    {
        const string source = """
            using SparseFragments;
            [SparseFragmentModel]
            public partial class SettingsA
            {
                public SharedChild Child { get; set; } = new();
            }
            [SparseFragmentModel]
            public partial class SettingsB
            {
                public SharedChild Child { get; set; } = new();
            }
            public partial class SharedChild
            {
                public int Value { get; set; }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new SparseFragments.Generator.SparseFragmentsGenerator().AsSourceGenerator()]
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(tree),
            out var output,
            out var diagnostics
        );
        diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        var generated = GetGeneratedSource(output, tree);
        generated.ShouldContain("SharedChild.Fragment");
        var count = generated.Split(["partial class SharedChild"], StringSplitOptions.None).Length - 1;
        count.ShouldBe(1);
        output.Emit(Stream.Null).Success.ShouldBeTrue(BuildDiagnosticMessage(output.GetDiagnostics()));
    }

    [Test]
    public void Sparse_ExplicitRootChild_NoDuplicate()
    {
        const string source = """
            using SparseFragments;
            [SparseFragmentModel]
            public partial class ExplicitRoot
            {
                public ExplicitChild Child { get; set; } = new();
            }
            [SparseFragmentModel]
            public partial class ExplicitChild
            {
                public int Value { get; set; }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new SparseFragments.Generator.SparseFragmentsGenerator().AsSourceGenerator()]
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(tree),
            out var output,
            out var diagnostics
        );
        diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        var result = driver.GetRunResult().Results.Single();
        result.GeneratedSources.Count(source => source.HintName?.Contains("SparsePromoted") == true).ShouldBe(0);
        var generated = GetGeneratedSource(output, tree);
        generated.ShouldContain("ExplicitChild.Fragment");
        output.Emit(Stream.Null).Success.ShouldBeTrue();
    }

    [Test]
    public void Configlue_PartialChild_GetsFragmentWithoutBecomingRoot()
    {
        const string source = """
            using Configlue;
            [ConfiglueModel("settings")]
            public partial class Settings
            {
                public DatabaseSettings Database { get; set; } = new();
            }
            public partial class DatabaseSettings
            {
                public string Host { get; set; } = "";
            }
            """;
        var (output, diagnostics, generated, driver) = RunConfiglue(source);
        diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        generated.ShouldContain("partial class DatabaseSettings");
        generated.ShouldContain("class Fragment");
        generated.ShouldContain("class Patch");
        generated.ShouldContain("DatabaseSettings.Fragment");
        generated.ShouldNotContain("__ConfiglueStructural_");
        // Promoted child must not become an independent root.
        var promoted = driver
            .GetRunResult()
            .Results.Single()
            .GeneratedSources.Where(source => source.HintName?.Contains("Promoted") == true)
            .Select(source => source.SourceText.ToString())
            .ToArray();
        promoted.Length.ShouldBe(1);
        promoted[0].ShouldNotContain("__Descriptor");
        promoted[0].ShouldNotContain("IConfiglueModel<");
        promoted[0].ShouldNotContain("PatchOptionsExtensions");
        output.Emit(Stream.Null).Success.ShouldBeTrue(BuildDiagnosticMessage(output.GetDiagnostics()));
    }

    [Test]
    public void Configlue_NonPartial_StaysStructural()
    {
        const string source = """
            using Configlue;
            [ConfiglueModel("settings")]
            public partial class Settings
            {
                public Holder Holder { get; set; } = new();
            }
            public partial class Holder
            {
                public NonPartialLeaf Item { get; set; } = new();
            }
            public class NonPartialLeaf
            {
                public string? Value { get; set; }
            }
            """;
        var (output, diagnostics, generated, _) = RunConfiglue(source);
        diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        generated.ShouldContain("__ConfiglueStructural_");
        generated.ShouldNotContain("partial class NonPartialLeaf");
        output.Emit(Stream.Null).Success.ShouldBeTrue();
    }

    [Test]
    public void Configlue_MultipleRoots_Dedupe()
    {
        const string source = """
            using Configlue;
            [ConfiglueModel("a")]
            public partial class SettingsA
            {
                public SharedChild Child { get; set; } = new();
            }
            [ConfiglueModel("b")]
            public partial class SettingsB
            {
                public SharedChild Child { get; set; } = new();
            }
            public partial class SharedChild
            {
                public int Value { get; set; }
            }
            """;
        var (output, diagnostics, generated, _) = RunConfiglue(source);
        diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        var count = generated.Split(["partial class SharedChild"], StringSplitOptions.None).Length - 1;
        count.ShouldBe(1);
        output.Emit(Stream.Null).Success.ShouldBeTrue();
    }

    [Test]
    public void Configlue_ExplicitRoot_NoDuplicate()
    {
        const string source = """
            using Configlue;
            [ConfiglueModel("root")]
            public partial class ExplicitRoot
            {
                public ExplicitChild Child { get; set; } = new();
            }
            [ConfiglueModel("child")]
            public partial class ExplicitChild
            {
                public int Value { get; set; }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ConfiglueGenerator().AsSourceGenerator()]
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(tree),
            out var output,
            out var diagnostics
        );
        diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        var result = driver.GetRunResult().Results.Single();
        result.GeneratedSources.Count(source => source.HintName?.Contains("Promoted") == true).ShouldBe(0);
        output.Emit(Stream.Null).Success.ShouldBeTrue();
    }
}
