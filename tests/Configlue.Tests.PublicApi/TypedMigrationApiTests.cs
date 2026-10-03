using System.Collections.Immutable;
using System.Reflection;
using Configlue;
using Configlue.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Configlue.Tests.PublicApi;

public sealed class TypedMigrationApiTests
{
    [Test]
    public async Task TypedProjectionAcceptsTheSelectedModelsGeneratedFragment()
    {
        var diagnostics = Compile(
            """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Configlue;
            using Configlue.Migrations;

            [ConfiglueModel("model-a")]
            public partial class ModelA { public int Value { get; set; } }

            public static class MigrationUse
            {
                public static ValueTask<StateStorageMigrationResult> Run(IConfiglueSources<ModelA> sources)
                {
                    var projections = new Dictionary<SourceKey<ModelA>, System.Func<ModelA.Fragment, ModelA.Fragment>>
                    {
                        [SourceKey<ModelA>.Named("target")] = static fragment => fragment,
                    };
                    return sources.MigrateSourcesToTargetsAsync(
                        [SourceKey<ModelA>.Named("legacy")],
                        projections
                    );
                }
            }
            """
        );

        diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();
    }

    [Test]
    public async Task TypedProjectionRejectsAnUnrelatedModelsFragmentAtCompileTime()
    {
        var diagnostics = Compile(
            """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Configlue;
            using Configlue.Migrations;

            [ConfiglueModel("model-a")]
            public partial class ModelA { public int Value { get; set; } }

            [ConfiglueModel("model-b")]
            public partial class ModelB { public string? Name { get; set; } }

            public static class MigrationUse
            {
                public static ValueTask<StateStorageMigrationResult> Run(IConfiglueSources<ModelA> sources)
                {
                    var projections = new Dictionary<SourceKey<ModelA>, System.Func<ModelB.Fragment, ModelB.Fragment>>
                    {
                        [SourceKey<ModelA>.Named("target")] = static fragment => fragment,
                    };
                    return sources.MigrateSourcesToTargetsAsync(
                        [SourceKey<ModelA>.Named("legacy")],
                        projections
                    );
                }
            }
            """
        );

        diagnostics
            .Any(static diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Id is "CS1929" or "CS1061" or "CS0311"
            )
            .ShouldBeTrue();
    }

    [Test]
    public async Task TypedWriteApisAcceptTheirModelsGeneratedPatch()
    {
        var diagnostics = Compile(
            """
            using System.Threading.Tasks;
            using Configlue;

            [ConfiglueModel("model-a")]
            public partial class ModelA { public int Value { get; set; } }

            public static class WriteUse
            {
                public static ValueTask<StateWriteReceipt> Save(
                    IWritableState<ModelA> state,
                    ConfiglueSourceHandle<ModelA> source
                )
                {
                    var patch = new ModelA.Patch();
                    return state.SaveAsync(patch);
                }

                public static ValueTask<StateWriteReceipt> SaveToSource(
                    IWritableState<ModelA> state,
                    ConfiglueSourceHandle<ModelA> source
                ) => source.SaveAsync(new ModelA.Patch());
            }
            """
        );

        diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();
    }

    [Test]
    public async Task TypedWriteApisRejectAnUnrelatedModelsGeneratedPatchAtCompileTime()
    {
        var diagnostics = Compile(
            """
            using System.Threading.Tasks;
            using Configlue;

            [ConfiglueModel("model-a")]
            public partial class ModelA { public int Value { get; set; } }

            [ConfiglueModel("model-b")]
            public partial class ModelB { public string? Name { get; set; } }

            public static class WriteUse
            {
                public static ValueTask<StateWriteReceipt> SaveWrong(
                    IWritableState<ModelB> state
                ) => state.SaveAsync(new ModelA.Patch());

                public static ValueTask<StateWriteReceipt> SaveToWrongSource(
                    ConfiglueSourceHandle<ModelB> source
                ) => source.SaveAsync(new ModelA.Patch());
            }
            """
        );

        diagnostics
            .Count(static diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == "CS1503"
            )
            .ShouldBe(2);
    }

    private static ImmutableArray<Diagnostic> Compile(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "TypedMigrationApiCompilation",
            [syntaxTree],
            BuildReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ConfiglueGenerator().AsSourceGenerator()],
            parseOptions: parseOptions
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics
        );
        _ = driver;
        return outputCompilation.GetDiagnostics().AddRange(generatorDiagnostics);
    }

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddReference(builder, seen, typeof(object).Assembly);
        AddReference(builder, seen, typeof(ConfiglueModelAttribute).Assembly);
        AddReference(builder, seen, typeof(ValueTask).Assembly);
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
