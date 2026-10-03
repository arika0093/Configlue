using Configlue.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Configlue.Tests.PublicApi;

public sealed partial class ConfiglueGeneratorDiagnosticTests
{
    [Test]
    [Arguments("Fragment")]
    [Arguments("PropertyChanged")]
    [Arguments("From")]
    [Arguments("Apply")]
    [Arguments("ToModel")]
    [Arguments("SelectMembers")]
    [Arguments("JsonConverter")]
    [Arguments("FragmentSchema")]
    [Arguments("Leaf")]
    [Arguments("ClonePatch")]
    public void ReservedPropertiesReportDiagnosticWithoutGeneratedSource(string name)
    {
        var result = RunGenerator(
            $$"""
            using Configlue;
            [ConfiglueModel("collision")]
            public partial class Settings { public int {{name}} { get; set; } }
            """
        );
        result.Diagnostics.ShouldContain(d => d.Id == "CFG013");
        result.GeneratedTrees.ShouldBeEmpty();
    }

    [Test]
    [Arguments("private class Fragment { }")]
    [Arguments("private int Patch;")]
    [Arguments("private void Details() { }")]
    public void NonPropertyRootDeclarationsReportCollision(string declaration)
    {
        var result = RunGenerator(
            $$"""
            using Configlue;
            [ConfiglueModel("collision")]
            public partial class Settings { {{declaration}} }
            """
        );
        result.Diagnostics.ShouldContain(d => d.Id == "CFG013");
        result.GeneratedTrees.ShouldBeEmpty();
    }

    [Test]
    public void StructuralAndSynthesizedObservableNamesReportCollision()
    {
        foreach (
            var declaration in new[]
            {
                "public int PropertyChanged { get; set; }",
                "public Child Child { get; set; } = new(); public int SetChild { get; set; }",
            }
        )
        {
            var result = RunGenerator(
                $$"""
                using Configlue;
                [ConfiglueModel("collision")]
                public partial class Settings { public Child Child { get; set; } = new(); }
                public class Child { {{declaration}} }
                """
            );
            result.Diagnostics.ShouldContain(d => d.Id == "CFG013");
            result.GeneratedTrees.ShouldBeEmpty();
        }
    }

    [Test]
    [Arguments("file partial class")]
    [Arguments("public ref partial struct")]
    public void UnsupportedRootShapesProduceOnlyGeneratorDiagnostic(string shape)
    {
        var result = RunGenerator(
            $$"""
            using Configlue;
            [ConfiglueModel("shape")]
            {{shape}} Settings { public int Value { get; init; } }
            """
        );
        result.Diagnostics.ShouldContain(d => d.Id == "CFG002");
        result.GeneratedTrees.ShouldBeEmpty();
    }

    [Test]
    [Arguments("internal partial class")]
    [Arguments("public partial class")]
    [Arguments("public partial struct")]
    [Arguments("public readonly partial struct")]
    [Arguments("public partial record")]
    [Arguments("public readonly partial record struct")]
    public void SupportedRootShapesCompile(string shape)
    {
        AssertGeneratedCompilation(
            $$"""
            using Configlue;
            [ConfiglueModel("shape")]
            {{shape}} Settings { public int Value { get; init; } }
            """
        );
    }

    [Test]
    public void PublicCurrentModelWithInternalPreviousVersionCompiles()
    {
        AssertGeneratedCompilation(
            """
                using Configlue;
                [ConfiglueModel("history", Version = 2)]
                [ConfigluePreviousVersion(typeof(Previous))]
                public partial class Settings { public int Value { get; set; } }
                [ConfiglueModel("history", Version = 1)]
                internal partial class Previous { public int Value { get; set; } }
            """
        );
    }

    [Test]
    public void InvalidChildModelProducesTargetedDiagnosticWithoutGeneratedCode()
    {
        var result = RunGenerator(
            """
            using Configlue;
            [ConfiglueModel("parent")]
            public partial class Settings { public Child<string> Child { get; set; } = new(); }
            [ConfiglueModel("child")]
            public partial class Child<T> { public T Value { get; set; } = default!; }
            """
        );

        result.Diagnostics.ShouldContain(d => d.Id == "CFG015");
        result.Diagnostics.ShouldContain(d => d.Id == "CFG002");
        result.GeneratedTrees.ShouldBeEmpty();
    }

    [Test]
    public void InvalidPreviousModelProducesTargetedDiagnosticWithoutGeneratedCode()
    {
        var result = RunGenerator(
            """
            using Configlue;
            [ConfiglueModel("history", Version = 2)]
            [ConfigluePreviousVersion(typeof(Previous))]
            public partial class Settings { public int Value { get; set; } }
            [ConfiglueModel("history", Version = 1)]
            public class Previous { public int Value { get; set; } }
            """
        );

        result.Diagnostics.ShouldContain(d => d.Id == "CFG015");
        result.Diagnostics.ShouldContain(d => d.Id == "CFG001");
        result.GeneratedTrees.ShouldBeEmpty();
    }

    [Test]
    public void KeywordChildMemberCompiles()
    {
        AssertGeneratedCompilation(
            """
            using Configlue;
            [ConfiglueModel("keyword")]
            public partial class Settings { public Child @class { get; set; } = new(); }
            public class Child { public int Value { get; set; } }
            """
        );
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ExplicitJsonNameCannotCollideWithExplicitOrDefaultName(bool explicitSecond)
    {
        var secondAttribute = explicitSecond
            ? "[System.Text.Json.Serialization.JsonPropertyName(\"B\")]"
            : "";
        var result = RunGenerator(
            $$"""
            using Configlue;
            [ConfiglueModel("json")]
            public partial class Settings {
                [System.Text.Json.Serialization.JsonPropertyName("B")]
                public int A { get; set; }
                {{secondAttribute}} public int B { get; set; }
            }
            """
        );
        result.Diagnostics.ShouldContain(d => d.Id == "CFG012");
        result.GeneratedTrees.ShouldBeEmpty();
    }

    [Test]
    [Arguments("SettingsDetailsExtensions")]
    [Arguments("SettingsPatchOptionsExtensions")]
    public void ExistingNamespaceTypesCannotCollideWithExtensionContainers(string name)
    {
        var result = RunGenerator(
            $$"""
            using Configlue;
            [ConfiglueModel("collision")]
            public partial class Settings { public int Value { get; set; } }
            public class {{name}} { }
            """
        );
        result.Diagnostics.ShouldContain(d => d.Id == "CFG013");
        result.GeneratedTrees.ShouldBeEmpty();
    }

    private static void AssertGeneratedCompilation(string source)
    {
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(source, options));
        var driver = CSharpGeneratorDriver.Create(
            new[] { new ConfiglueGenerator().AsSourceGenerator() },
            parseOptions: options
        );
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        output
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ShouldBeEmpty();
    }
}
