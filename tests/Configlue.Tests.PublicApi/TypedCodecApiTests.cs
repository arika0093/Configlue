using System.Collections.Immutable;
using System.Reflection;
using Configlue;
using Configlue.Codecs;
using Configlue.Extensibility;
using Configlue.Generator;
using Configlue.Hosting.Blazor;
using Configlue.Resource.Redis;
using Configlue.Resource.S3;
using Configlue.Resource.Vault;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Configlue.Tests.PublicApi;

public sealed class TypedCodecApiTests
{
    [Test]
    public async Task SerializedSourcesAndProviderOptionsRejectUnrelatedCodecObjectsAtCompileTime()
    {
        var diagnostics = Compile(
            """
            using Configlue.Codecs;
            using Configlue.Extensibility;
            using Configlue.Hosting.Blazor;
            using Configlue.Resource.Redis;
            using Configlue.Resource.S3;
            using Configlue.Resource.Vault;
            using Configlue.Resources;

            public static class InvalidCodecUse
            {
                public static object Create(IResourceReader resource)
                {
                    var arbitrary = new object();
                    _ = new SerializedSource<string>(resource, arbitrary);
                    _ = new RedisStateSourceOptions { ResourceNamespace = "settings", Codec = arbitrary };
                    _ = new S3ObjectSourceOptions { BucketName = "bucket", Key = "settings", Codec = arbitrary };
                    _ = new VaultKvSourceOptions { Mount = "secret", Path = "settings", Codec = arbitrary };
                    _ = new WebStorageSourceOptions { Key = "settings", Codec = arbitrary };
                    return arbitrary;
                }
            }
            """
        );

        diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id)
            .ShouldContain("CS1503");
        diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Count(static diagnostic => diagnostic.Id is "CS0029" or "CS0266")
            .ShouldBeGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task TypedAndDynamicCodecPathsAreExplicitAndCompileForDualContractCodec()
    {
        var diagnostics = Compile(
            """
            using System.Buffers;
            using Configlue.Codecs;
            using Configlue.Extensibility;
            using Configlue.Resources;

            public sealed class DualCodec : IStateCodec<string>, IStateCodec
            {
                public string? Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context) => "value";
                public void Serialize(string? value, IBufferWriter<byte> destination, in StateCodecContext context) { }
                object? IStateCodec.Deserialize(System.Type type, in ReadOnlySequence<byte> source, in StateCodecContext context) => "value";
                void IStateCodec.Serialize(System.Type type, object? value, IBufferWriter<byte> destination, in StateCodecContext context) { }
            }

            public static class ValidCodecUse
            {
                public static SerializedSource<string> Typed(IResourceReader resource, DualCodec codec) =>
                    new(resource, codec);

                public static SerializedSource<string> Dynamic(IResourceReader resource, DualCodec codec) =>
                    new(resource, StateCodecBinding.Dynamic(codec));

                public static StateCodecBinding ExplicitTyped(DualCodec codec) => StateCodecBinding.Typed<string>(codec);
            }
            """
        );

        diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();
    }

    private static ImmutableArray<Diagnostic> Compile(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "TypedCodecApiCompilation",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
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
        AddReference(builder, seen, typeof(SerializedSource<>).Assembly);
        AddReference(builder, seen, typeof(RedisStateSourceOptions).Assembly);
        AddReference(builder, seen, typeof(S3ObjectSourceOptions).Assembly);
        AddReference(builder, seen, typeof(VaultKvSourceOptions).Assembly);
        AddReference(builder, seen, typeof(WebStorageSourceOptions).Assembly);
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
