using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using Configlue;
using Configlue.Extensions.MSOptions;
using Configlue.Generator;
using Configlue.JsonSchema;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Resource.Dapr;
using Configlue.Resource.Http;
using Configlue.Resource.Http.AspNetCore;
using Configlue.Resource.S3;
using Configlue.Resource.Zip;
using Configlue.Source.CommandLine;
using Configlue.Source.Environment;
using Configlue.Source.Presets;
using Configlue.Testing;
using Configlue.Transformer.AES;
using PublicApiGenerator;

namespace Configlue.Tests.PublicApi;

public static class PublicApiCheck
{
    private static readonly object ApprovalUpdateGate = new();
    private const string UpdateApprovalsEnvironmentVariable = "CONFIGLUE_UPDATE_PUBLIC_API";

    public static void Check<T>() => Check(typeof(T).Assembly);

    public static void CheckAssembly(Assembly assembly) => Check(assembly);

    public static void CheckCompiler(Assembly assembly) => Check(assembly, compilerOnly: true);

    private static void Check(Assembly assembly, bool compilerOnly = false)
    {
        var assemblyName =
            assembly.GetName().Name! + (compilerOnly ? ".CompilerServices" : string.Empty);
        var publicApi = assembly.GeneratePublicApi(
            new()
            {
                IncludeTypes = assembly
                    .GetExportedTypes()
                    .Where(type => (type.Namespace == "Configlue.CompilerServices") == compilerOnly)
                    .ToArray(),
                ExcludeAttributes =
                [
                    typeof(InternalsVisibleToAttribute).FullName!,
                    typeof(TargetFrameworkAttribute).FullName!,
                ],
            }
        );
        if (compilerOnly)
        {
            publicApi += FormatFacadeModelStaticMemberModifiers(assembly);
        }

        if (Environment.GetEnvironmentVariable(UpdateApprovalsEnvironmentVariable) == "1")
        {
            var sourceApproval = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "../../../Approvals",
                    $"{assemblyName}.approved.txt"
                )
            );
            Directory.CreateDirectory(Path.GetDirectoryName(sourceApproval)!);
            lock (ApprovalUpdateGate)
            {
                File.WriteAllText(
                    sourceApproval,
                    publicApi,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                );
            }
            return;
        }

        var approvedApi = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Approvals", $"{assemblyName}.approved.txt")
        );
        publicApi.ShouldBe(approvedApi);
    }

    private static string FormatFacadeModelStaticMemberModifiers(Assembly assembly)
    {
        var contract = assembly.GetType("Configlue.CompilerServices.IConfiglueFacadeModel`1");
        if (contract is null)
        {
            return string.Empty;
        }

        var members = contract
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .Select(method =>
            {
                var modifiers = new List<string> { "static" };
                if (method.IsAbstract)
                {
                    modifiers.Add("abstract");
                }

                if (method.IsVirtual)
                {
                    modifiers.Add("virtual");
                }

                var interfaceName = contract.Name[..contract.Name.IndexOf('`')];
                return $"{interfaceName}.{method.Name}: {string.Join(' ', modifiers)}";
            });

        return $"{Environment.NewLine}// Compiled static interface member modifiers{Environment.NewLine}{string.Join(Environment.NewLine, members)}{Environment.NewLine}";
    }
}

public sealed class PublicApiCheckTest
{
    [Test]
    public void Abstraction() => PublicApiCheck.Check<ConfiglueModelAttribute>();

    [Test]
    public void Extensibility() => PublicApiCheck.Check<SerializedStateReader<object>>();

    [Test]
    public void Core() => PublicApiCheck.CheckAssembly(typeof(ConfiglueOptions<,>).Assembly);

    [Test]
    public void CompilerAbstraction() =>
        PublicApiCheck.CheckCompiler(typeof(IConfiglueModel<,>).Assembly);

    [Test]
    public void CompilerRuntime() =>
        PublicApiCheck.CheckCompiler(typeof(IConfiglueFacadeModel<>).Assembly);

    [Test]
    public void DependencyInjection() =>
        PublicApiCheck.CheckAssembly(typeof(ConfiglueServiceCollectionExtensions).Assembly);

    [Test]
    public void MicrosoftOptions() =>
        PublicApiCheck.CheckAssembly(
            typeof(ConfiglueMicrosoftOptionsServiceCollectionExtensions).Assembly
        );

    [Test]
    public void R3Integration() =>
        PublicApiCheck.CheckAssembly(
            typeof(global::Configlue.Extensions.R3.ConfiglueR3Extensions).Assembly
        );

    [Test]
    public void Generator() => PublicApiCheck.Check<ConfiglueGenerator>();

    [Test]
    public void Json() => PublicApiCheck.Check<JsonStateCodec<object>>();

    [Test]
    public void JsonSchema() => PublicApiCheck.Check<JsonSchemaGenerationResult>();

    [Test]
    public void Xml() => PublicApiCheck.Check<XmlStateCodec<object>>();

    [Test]
    public void Yaml() => PublicApiCheck.Check<YamlStateCodec<object>>();

    [Test]
    public void Environment() =>
        PublicApiCheck.CheckAssembly(typeof(EnvironmentStateSource).Assembly);

    [Test]
    public void CommonSources() => PublicApiCheck.Check<CommonSourceBuilder>();

    [Test]
    public void CommonXmlSources() =>
        PublicApiCheck.CheckAssembly(typeof(CommonXmlFileSourceExtensions).Assembly);

    [Test]
    public void CommonYamlSources() =>
        PublicApiCheck.CheckAssembly(typeof(CommonYamlFileSourceExtensions).Assembly);

    [Test]
    public void SingleBinary() => PublicApiCheck.Check<SingleBinaryBuilder>();

    [Test]
    public void CommandLineSources() => PublicApiCheck.Check<CommandLineSourceOptions>();

    [Test]
    public void Zip() => PublicApiCheck.Check<ZipEntryResource>();

    [Test]
    public void Http() => PublicApiCheck.Check<HttpResourceReader>();

    [Test]
    public void HttpAspNetCore() => PublicApiCheck.Check<HttpResourceEndpointOptions>();

    [Test]
    public void Dapr() => PublicApiCheck.Check<DaprStateSourceOptions>();

    [Test]
    public void S3() => PublicApiCheck.Check<S3ObjectSourceOptions>();

    [Test]
    public void Testing() => PublicApiCheck.Check<InMemoryResource>();

    [Test]
    public void AesTransformer() => PublicApiCheck.Check<AesGcmStateByteTransformer>();

    [Test]
    public void AesPassphraseTransformer() =>
        PublicApiCheck.Check<AesGcmPassphraseStateByteTransformer>();
}
