using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using Configlue;
using Configlue.CompilerServices;
using Configlue.Extensions.ComponentModel;
using Configlue.Extensions.MSOptions;
using Configlue.Generator;
using Configlue.Hosting.AspNetCore;
using Configlue.Hosting.Blazor;
using Configlue.Provider.Json;
using Configlue.Provider.MessagePack;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Source.Http;
using Configlue.Resource.AzureBlob;
using Configlue.Resource.Redis;
using Configlue.Resource.Etcd;
using Configlue.Resource.S3;
using Configlue.Resource.Vault;
using Configlue.Resource.GoogleSecretManager;
using Configlue.Resource.Kubernetes;
using Configlue.Resource.Gcs;
using Configlue.Resource.Zip;
using Configlue.Source.CommandLine;
using Configlue.Source.Consul;
using Configlue.Source.Environment;
using Configlue.Source.Http;
using Configlue.Source.PostgreSql;
using Configlue.Source.PostgreSql.Migrations;
using Configlue.Source.Presets;
using Configlue.Testing;
using Configlue.Transformer.AES;
using Configlue.Transformer.Compression;
using PublicApiGenerator;

namespace Configlue.Tests.PublicApi;

public static class PublicApiCheck
{
    private static readonly object ApprovalUpdateGate = new();
    private const string UpdateApprovalsEnvironmentVariable = "CONFIGLUE_UPDATE_PUBLIC_API";

    public static void Check<T>() => Check(typeof(T).Assembly);

    public static void Check<T>(string approvalName, Func<Type, bool> includeType) =>
        Check(typeof(T).Assembly, approvalName: approvalName, includeType: includeType);

    public static void CheckAssembly(Assembly assembly) => Check(assembly);

    public static void CheckAssembly(
        Assembly assembly,
        string approvalName,
        Func<Type, bool> includeType
    ) => Check(assembly, approvalName: approvalName, includeType: includeType);

    public static void CheckCompiler(Assembly assembly) => Check(assembly, compilerOnly: true);

    private static void Check(
        Assembly assembly,
        bool compilerOnly = false,
        string? approvalName = null,
        Func<Type, bool>? includeType = null
    )
    {
        var assemblyName =
            approvalName
            ?? assembly.GetName().Name! + (compilerOnly ? ".CompilerServices" : string.Empty);
        var publicApi = assembly.GeneratePublicApi(
            new()
            {
                IncludeTypes = assembly
                    .GetExportedTypes()
                    .Where(type =>
                        (type.Namespace == "Configlue.CompilerServices") == compilerOnly
                        && (includeType?.Invoke(type) ?? true)
                    )
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
            })
            .ToArray();

        return members.Length == 0
            ? string.Empty
            : $"{Environment.NewLine}// Compiled static interface member modifiers{Environment.NewLine}{string.Join(Environment.NewLine, members)}{Environment.NewLine}";
    }
}

public sealed class PublicApiCheckTest
{
    [Test]
    public void Abstraction() => PublicApiCheck.Check<ConfiglueModelAttribute>();

    [Test]
    public void StateSourceKeepsItsPublicConstructorSurfaceSmall() =>
        typeof(Configlue.Sources.StateSource<>).GetConstructors().Length.ShouldBe(3);

    [Test]
    public void StandaloneFragments() =>
        PublicApiCheck.Check<SparseFragments.SparseFragmentModelAttribute>();

    [Test]
    public void StandaloneGenerator() =>
        PublicApiCheck.Check<SparseFragments.Generator.SparseFragmentsGenerator>();

    [Test]
    public void Extensibility() =>
        PublicApiCheck.Check<SerializedStateReader<object>>(
            "Configlue.Extensibility",
            static type => type.Namespace == "Configlue.Extensibility"
        );

    [Test]
    public void Core() =>
        PublicApiCheck.CheckAssembly(
            typeof(ConfiglueApp).Assembly,
            "Configlue.Core",
            static type =>
                type.Namespace != "Configlue.Extensibility"
                && type.Namespace != "Configlue.Resource.Zip"
                && type != typeof(CommonFileSourceBuilder)
        );

    [Test]
    public void CommonFilePresetSpi() =>
        PublicApiCheck.Check<CommonFileSourceBuilder>(
            "Configlue.Core.Presets",
            static type => type == typeof(CommonFileSourceBuilder)
        );

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
    public void ComponentModel() =>
        PublicApiCheck.CheckAssembly(typeof(ConfiglueStateReader<>).Assembly);

    [Test]
    public void AvaloniaHosting() =>
        PublicApiCheck.Check<global::Configlue.Hosting.Avalonia.AvaloniaConfiglueDispatcher>();

    [Test]
    public void MauiHosting() =>
        PublicApiCheck.Check<global::Configlue.Hosting.Maui.MauiHostPaths>();

    [Test]
    public void GodotHosting() =>
        PublicApiCheck.Check<global::Configlue.Hosting.Godot.GodotHostPaths>();

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
    public void Json() =>
        PublicApiCheck.Check<JsonStateCodec<object>>(
            "Configlue.Provider.Json",
            static type => type != typeof(CommonJsonFileSourceExtensions)
        );

    [Test]
    public void CommonJsonSources() =>
        PublicApiCheck.CheckAssembly(
            typeof(CommonJsonFileSourceExtensions).Assembly,
            "Configlue.Provider.Json.Presets",
            static type => type == typeof(CommonJsonFileSourceExtensions)
        );

    [Test]
    public void MessagePack() =>
        PublicApiCheck.Check<MessagePackStateCodec<object>>(
            "Configlue.Provider.MessagePack",
            static type => type.Namespace == "Configlue.Provider.MessagePack"
        );

    [Test]
    public void Xml() =>
        PublicApiCheck.Check<XmlStateCodec<object>>(
            "Configlue.Provider.Xml",
            static type => type.Namespace == "Configlue.Provider.Xml"
        );

    [Test]
    public void Yaml() =>
        PublicApiCheck.Check<YamlStateCodec<object>>(
            "Configlue.Provider.Yaml",
            static type => type.Namespace == "Configlue.Provider.Yaml"
        );

    [Test]
    public void Environment() =>
        PublicApiCheck.CheckAssembly(typeof(EnvironmentStateSource).Assembly);

    [Test]
    public void CommonSources() =>
        PublicApiCheck.Check<CommonSourceBuilder>(
            "Configlue.Source.Presets",
            static type => type.Namespace == "Configlue.Source.Presets"
        );

    [Test]
    public void CommonXmlSources() =>
        PublicApiCheck.CheckAssembly(
            typeof(CommonXmlFileSourceExtensions).Assembly,
            "Configlue.Source.Presets.Xml",
            static type => type == typeof(CommonXmlFileSourceExtensions)
        );

    [Test]
    public void CommonYamlSources() =>
        PublicApiCheck.CheckAssembly(
            typeof(CommonYamlFileSourceExtensions).Assembly,
            "Configlue.Source.Presets.Yaml",
            static type => type == typeof(CommonYamlFileSourceExtensions)
        );

    [Test]
    public void CommandLineSources() => PublicApiCheck.Check<CommandLineSourceOptions>();

    [Test]
    public void Zip() =>
        PublicApiCheck.Check<ZipEntryResource>(
            "Configlue.Resource.Zip",
            static type => type.Namespace == "Configlue.Resource.Zip"
        );

    [Test]
    public void HttpState() => PublicApiCheck.Check<HttpStateSourceOptions>();

    [Test]
    public void AspNetCoreHost() =>
        PublicApiCheck.CheckAssembly(typeof(HttpContextConfiglueSubjectAccessor<>).Assembly);

    [Test]
    public void BlazorHost() =>
        PublicApiCheck.CheckAssembly(
            typeof(BlazorAuthenticationConfiglueSubjectAccessor<>).Assembly
        );

    [Test]
    public void S3() => PublicApiCheck.Check<S3ObjectSourceOptions>();

    [Test]
    public void AwsAppConfig() =>
        PublicApiCheck.Check<Configlue.Resource.AwsAppConfig.AwsAppConfigSourceOptions>();
    public void AzureBlob() => PublicApiCheck.Check<AzureBlobSourceOptions>();
    public void Gcs() => PublicApiCheck.Check<GcsObjectSourceOptions>();

    [Test]
    public void PostgreSql() => PublicApiCheck.Check<PostgreSqlSourceOptions>();

    [Test]
    public void PostgreSqlMigrations() => PublicApiCheck.Check<PostgreSqlSchemaMigrator>();

    [Test]
    public void Redis() => PublicApiCheck.Check<RedisStateSourceOptions>();

    [Test]
    public void Vault() => PublicApiCheck.Check<VaultKvSourceOptions>();

    [Test]
    public void Consul() => PublicApiCheck.Check<ConsulKvPrefixSourceOptions>();
    public void Etcd() => PublicApiCheck.Check<EtcdStateSourceOptions>();
    public void GoogleSecretManager() =>
        PublicApiCheck.Check<GoogleSecretManagerSourceOptions>();
    public void Kubernetes() => PublicApiCheck.Check<KubernetesSourceOptions>();

    [Test]
    public void Testing() => PublicApiCheck.Check<InMemoryResource>();

    [Test]
    public void AesTransformer() => PublicApiCheck.Check<AesGcmStateByteTransformer>();

    [Test]
    public void AesPassphraseTransformer() =>
        PublicApiCheck.Check<AesGcmPassphraseStateByteTransformer>();

    [Test]
    public void CompressionTransformer() =>
        PublicApiCheck.Check<CompressionStateByteTransformer>(
            "Configlue.Transformer.Compression",
            static type => type.Namespace == "Configlue.Transformer.Compression"
        );
}
