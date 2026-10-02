using System.Collections.Immutable;
using System.Text;
using Configlue.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Configlue.Generator.Compatibility.Tests;

public sealed class GeneratorHostCompatibilityTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void RequiredFieldHasActionableConstructionDiagnostic(bool standalone)
    {
        var runtime = standalone ? "SparseFragments" : "Configlue";
        var attribute = standalone ? "SparseFragmentModel" : "ConfiglueModel(\"required-field\")";
        var source = $$"""
            using {{runtime}};
            [{{attribute}}]
            public partial class Settings { public required int Value; }
            """;
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(source, options));
        IIncrementalGenerator generator = standalone
            ? new SparseFragments.Generator.SparseFragmentsGenerator()
            : new ConfiglueGenerator();
        var result = CSharpGeneratorDriver
            .Create(new[] { generator.AsSourceGenerator() }, parseOptions: options)
            .RunGenerators(compilation)
            .GetRunResult();
        result.Results.Single().Exception.ShouldBeNull();
        var diagnostic = result.Diagnostics.Single(d => d.Id == (standalone ? "SPF006" : "CFG004"));
        diagnostic.GetMessage().ShouldContain("public property");
        diagnostic.GetMessage().ShouldContain("Value");
    }

    [Test]
    [Arguments(false, false, false, false)]
    [Arguments(false, false, false, true)]
    [Arguments(false, false, true, false)]
    [Arguments(false, false, true, true)]
    [Arguments(false, true, false, false)]
    [Arguments(false, true, false, true)]
    [Arguments(false, true, true, false)]
    [Arguments(false, true, true, true)]
    [Arguments(true, false, false, false)]
    [Arguments(true, false, false, true)]
    [Arguments(true, false, true, false)]
    [Arguments(true, false, true, true)]
    [Arguments(true, true, false, false)]
    [Arguments(true, true, false, true)]
    [Arguments(true, true, true, false)]
    [Arguments(true, true, true, true)]
    public void PrivateRootConstructorAndSparseDefaultsConstructOnce(
        bool standalone,
        bool initOnly,
        bool required,
        bool parameterized
    )
    {
        var constructor = parameterized
            ? "private Settings(int count = 5, int[]? items = null) { Identity = ++Calls; Count = count; Items = items; LastItems = items; }"
            : "private Settings() { Identity = ++Calls; }";
        var requiredKeyword = required ? "required " : string.Empty;
        var setter = initOnly ? "init" : "set";
        var runtime = standalone ? "SparseFragments" : "Configlue";
        var attribute = standalone
            ? "SparseFragmentModel"
            : "ConfiglueModel(\"constructor-parity\")";
        var observableCheck = standalone
            ? string.Empty
            : "if (typeof(Settings.Observable).GetProperty(nameof(Settings.Count))!.CanWrite != "
                + (initOnly ? "false" : "true")
                + ") throw new System.Exception(\"observable mutability\");";
        var source = $$"""
            using {{runtime}};
            [{{attribute}}]
            public partial class Settings
            {
                public static int Calls;
                public static int[]? LastItems;
                {{constructor}}
                public {{requiredKeyword}}int Identity { get; {{setter}}; }
                public {{requiredKeyword}}int Count { get; {{setter}}; } = 5;
                public int[]? Items { get; {{setter}}; }
            }
            public static class Probe
            {
                public static string Run()
                {
                    {{observableCheck}}
                    var empty = Settings.Fragment.Empty.ToModel();
                    if (Settings.Calls != 1 || empty.Identity != 1 || empty.Count != 5)
                        throw new System.Exception("empty projection");
                    var sparse = new Settings.Fragment { Count = Optional<int>.Present(0), Items = Optional<int[]?>.Present(new[] { 1, 2 }) }.ToModel();
                    if (Settings.Calls != 2 || sparse.Identity != 2 || sparse.Count != 0)
                        throw new System.Exception("sparse projection");
                    var complete = Settings.Fragment.From(sparse).ToModel();
                    if (Settings.Calls != 3 || complete.Identity != 2 || complete.Count != 0)
                        throw new System.Exception("complete projection");
                    var clone = complete.DeepClone();
                    if (Settings.Calls != 4 || clone.Identity != 2 || clone.Count != 0 || ReferenceEquals(clone, complete))
                        throw new System.Exception("clone construction");
                    if (ReferenceEquals(clone.Items, complete.Items) || clone.Items![0] != 1)
                        throw new System.Exception("clone isolation");
                    if ({{(
                parameterized ? "true" : "false"
            )}} && !ReferenceEquals(Settings.LastItems, clone.Items))
                        throw new System.Exception("constructor clone identity");
                    return "passed";
                }
            }
            """;
        // Roslyn 4.3.1 exposes C# 11 required members through its preview parser.
        var options = new CSharpParseOptions(
            required ? LanguageVersion.Preview : LanguageVersion.Latest
        );
        var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(source, options));
        IIncrementalGenerator generator = standalone
            ? new SparseFragments.Generator.SparseFragmentsGenerator()
            : new ConfiglueGenerator();
        CSharpGeneratorDriver
            .Create(new[] { generator.AsSourceGenerator() }, parseOptions: options)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        using var stream = new MemoryStream();
        var emission = output.Emit(stream);
        emission.Success.ShouldBeTrue(BuildDiagnosticMessage(emission.Diagnostics));
        var context = new System.Runtime.Loader.AssemblyLoadContext(
            Guid.NewGuid().ToString(),
            isCollectible: true
        );
        try
        {
            stream.Position = 0;
            var assembly = context.LoadFromStream(stream);
            assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null).ShouldBe("passed");
        }
        finally
        {
            context.Unload();
        }
    }

    [Test]
    public void SharedFragmentOperationsHaveRuntimeParity()
    {
        string? previousResult = null;
        foreach (var standalone in new[] { false, true })
        {
            var runtime = standalone ? "SparseFragments" : "Configlue";
            var modelAttribute = standalone
                ? "SparseFragmentModel"
                : "ConfiglueModel(\"algebra-parity\")";
            var mergeAttribute = standalone ? "SparseMerge" : "ConfiglueMerge";
            var source = $$"""
                using System;
                using System.Collections.Generic;
                using {{runtime}};
                [{{modelAttribute}}]
                public partial class Settings
                {
                    public int Count { get; set; } = 3;
                    public string? Label { get; set; } = "default";
                    public Child? Nested { get; set; } = new Child();
                    [{{mergeAttribute}}(MergeMode.Append)]
                    public List<int> Items { get; set; } = new List<int>();
                    [{{mergeAttribute}}(MergeMode.SetUnion)]
                    public ISet<string> Tags { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    public Dictionary<string, int> Map { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }
                public class Child
                {
                    public int First { get; set; } = 10;
                    public int Second { get; set; } = 20;
                }
                public static class Probe
                {
                    public static string Run()
                    {
                        var original = new Settings
                        {
                            Count = 4,
                            Nested = new Child { First = 11 },
                            Items = new List<int> { 1 },
                            Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alpha" },
                            Map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["first"] = 1, ["second"] = 2 },
                        };
                        var clone = original.DeepClone();
                        clone.Nested!.First = 90;
                        clone.Items.Add(90);
                        clone.Map["first"] = 90;
                        if (original.Nested!.First != 11 || original.Items.Count != 1 || original.Map["first"] != 1)
                            throw new Exception("model isolation");
                        var lower = Settings.Fragment.From(original);
                        original.Items.Add(100);
                        if (lower.Items.Value!.Count != 1) throw new Exception("From isolation");
                        var higher = new Settings.Fragment
                        {
                            Count = Optional<int>.Present(0),
                            Label = Optional<string?>.Present(null),
                            Items = Optional<List<int>>.Present(new List<int> { 2 }),
                            Tags = Optional<ISet<string>>.Present(new HashSet<string> { "ALPHA", "beta" }),
                        };
                        var merged = lower.Merge(higher).DeepClone().ToModel();
                        if (merged.Items.Count != 2 || merged.Tags.Count != 2 || !merged.Tags.Contains("BETA"))
                            throw new Exception("collection merge");
                        var changed = lower.ToModel();
                        changed.Nested = new Child { First = 11, Second = 21 };
                        changed.Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ALPHA" };
                        changed.Map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["SECOND"] = 2, ["FIRST"] = 1 };
                        var diff = Settings.Fragment.Diff(lower.ToModel(), changed);
                        if (diff.Tags.IsPresent || diff.Map.IsPresent || !diff.Nested.IsPresent)
                            throw new Exception("semantic diff");
                        var applied = lower.ApplyChanges(diff).ToModel();
                        var projected = new Settings.Fragment { Count = Optional<int>.Present(0) }.ToModel();
                        return string.Join("|", merged.Count, merged.Label is null, merged.Items.Count,
                            merged.Tags.Count, applied.Nested!.First, applied.Nested.Second,
                            projected.Count, projected.Label, lower.Count.Value);
                    }
                }
                """;
            var options = new CSharpParseOptions(LanguageVersion.Latest);
            var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(source, options));
            IIncrementalGenerator generator = standalone
                ? new SparseFragments.Generator.SparseFragmentsGenerator()
                : new ConfiglueGenerator();
            CSharpGeneratorDriver
                .Create(new[] { generator.AsSourceGenerator() }, parseOptions: options)
                .RunGeneratorsAndUpdateCompilation(
                    compilation,
                    out var output,
                    out var diagnostics
                );
            diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
            using var stream = new MemoryStream();
            var emission = output.Emit(stream);
            emission.Success.ShouldBeTrue(BuildDiagnosticMessage(emission.Diagnostics));
            var context = new System.Runtime.Loader.AssemblyLoadContext(
                Guid.NewGuid().ToString(),
                isCollectible: true
            );
            try
            {
                stream.Position = 0;
                var assembly = context.LoadFromStream(stream);
                var result = (string)
                    assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null)!;
                result.ShouldBe("0|True|2|2|11|21|0|default|4");
                if (previousResult is not null)
                    result.ShouldBe(previousResult);
                previousResult = result;
            }
            finally
            {
                context.Unload();
            }
        }
    }

    [Test]
    [Arguments("ISet<string>", "Append", false)]
    [Arguments("HashSet<string>", "Append", false)]
    [Arguments("ISet<string>", "SetUnion", true)]
    [Arguments("List<string>", "Append", true)]
    [Arguments("int", "Deep", false)]
    [Arguments("int", "Append", false)]
    public void BuiltInMergeValidationHasParity(string type, string mode, bool supported)
    {
        foreach (var standalone in new[] { false, true })
        {
            var runtime = standalone ? "SparseFragments" : "Configlue";
            var modelAttribute = standalone ? "SparseFragmentModel" : "ConfiglueModel(\"parity\")";
            var mergeAttribute = standalone ? "SparseMerge" : "ConfiglueMerge";
            var source = $$"""
                using {{runtime}};
                using System.Collections.Generic;
                [{{modelAttribute}}]
                public partial class Settings
                {
                    [{{mergeAttribute}}(MergeMode.{{mode}})]
                    public {{type}} Value { get; set; }
                }
                """;
            var options = new CSharpParseOptions(LanguageVersion.CSharp9);
            var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(source, options));
            IIncrementalGenerator generator = standalone
                ? new SparseFragments.Generator.SparseFragmentsGenerator()
                : new ConfiglueGenerator();
            var driver = CSharpGeneratorDriver.Create(
                new[] { generator.AsSourceGenerator() },
                parseOptions: options
            );
            var result = driver.RunGenerators(compilation).GetRunResult();

            result.Results.Single().Exception.ShouldBeNull();
            result
                .Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)
                .ShouldBe(!supported);
            if (!supported)
                result.Diagnostics.ShouldContain(d => d.Id == (standalone ? "SPF005" : "CFG005"));
        }
    }

    [Test]
    public void SparseGenerator_ReusesEquivalentAnalysis_AndInvalidatesChangedMembers()
    {
        const string source = """
            using SparseFragments;
            using System.Collections.Generic;
            [SparseFragmentModel]
            public partial class Settings
            {
                public Nested Child { get; set; }
                public List<Nested> Children { get; set; }
            }
            public class Nested { public int Value { get; set; } }
            """;
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp9);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CreateCompilation(tree);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new SparseFragments.Generator.SparseFragmentsGenerator().AsSourceGenerator() },
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, true)
        );
        driver = driver.RunGenerators(compilation);
        driver.GetRunResult().Results.Single().Exception.ShouldBeNull();
        var original = driver.GetRunResult().GeneratedTrees.Single().ToString();

        var equivalentTree = CSharpSyntaxTree.ParseText(source + "\n", parseOptions);
        compilation = compilation.ReplaceSyntaxTree(tree, equivalentTree);
        driver = driver.RunGenerators(compilation);
        var equivalent = driver.GetRunResult().Results.Single();
        equivalent.Exception.ShouldBeNull();
        equivalent
            .TrackedSteps["SparseFragmentsGenerator.Analysis"]
            .Single()
            .Outputs.Single()
            .Reason.ShouldBe(IncrementalStepRunReason.Unchanged);
        equivalent
            .TrackedSteps["SparseFragmentsGenerator.Output"]
            .Single()
            .Outputs.Single()
            .Reason.ShouldBe(IncrementalStepRunReason.Cached);

        var changedTree = CSharpSyntaxTree.ParseText(
            source.Replace("int Value", "string Value"),
            parseOptions
        );
        compilation = compilation.ReplaceSyntaxTree(equivalentTree, changedTree);
        driver = driver.RunGenerators(compilation);
        var changed = driver.GetRunResult().Results.Single();
        changed.Exception.ShouldBeNull();
        changed
            .TrackedSteps["SparseFragmentsGenerator.Output"]
            .Single()
            .Outputs.Single()
            .Reason.ShouldBe(IncrementalStepRunReason.Modified);
        driver.GetRunResult().GeneratedTrees.Single().ToString().ShouldNotBe(original);
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
