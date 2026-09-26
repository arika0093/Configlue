using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using Configlue;
using Configlue.Generator;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Resource.Http;
using Configlue.Resource.Http.AspNetCore;
using Configlue.Resource.Zip;
using Configlue.Source.Environment;
using Configlue.Testing;
using PublicApiGenerator;

namespace Configlue.Tests.PublicApi;

public static class PublicApiCheck
{
    private const string UpdateApprovalsEnvironmentVariable = "CONFIGLUE_UPDATE_PUBLIC_API";

    public static void Check<T>() => Check(typeof(T).Assembly);

    public static void CheckAssembly(Assembly assembly) => Check(assembly);

    private static void Check(Assembly assembly)
    {
        var assemblyName = assembly.GetName().Name!;
        var publicApi = assembly.GeneratePublicApi(
            new()
            {
                ExcludeAttributes =
                [
                    typeof(InternalsVisibleToAttribute).FullName!,
                    typeof(TargetFrameworkAttribute).FullName!,
                ],
            }
        );

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
            File.WriteAllText(
                sourceApproval,
                publicApi,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            );
            return;
        }

        var approvedApi = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Approvals", $"{assemblyName}.approved.txt")
        );
        publicApi.ShouldBe(approvedApi);
    }
}

public sealed class PublicApiCheckTest
{
    [Test]
    public void Abstraction() => PublicApiCheck.Check<ConfiglueModelAttribute>();

    [Test]
    public void Core() => PublicApiCheck.CheckAssembly(typeof(ConfiglueOptions<,>).Assembly);

    [Test]
    public void DependencyInjection() =>
        PublicApiCheck.CheckAssembly(typeof(ConfiglueServiceCollectionExtensions).Assembly);

    [Test]
    public void Generator() => PublicApiCheck.Check<ConfiglueGenerator>();

    [Test]
    public void Json() => PublicApiCheck.Check<JsonStateCodec<object>>();

    [Test]
    public void Xml() => PublicApiCheck.Check<XmlStateCodec<object>>();

    [Test]
    public void Yaml() => PublicApiCheck.Check<YamlStateCodec<object>>();

    [Test]
    public void Environment() =>
        PublicApiCheck.CheckAssembly(typeof(EnvironmentStateSource).Assembly);

    [Test]
    public void Zip() => PublicApiCheck.Check<ZipEntryResource>();

    [Test]
    public void Http() => PublicApiCheck.Check<HttpResourceReader>();

    [Test]
    public void HttpAspNetCore() => PublicApiCheck.Check<HttpResourceEndpointOptions>();

    [Test]
    public void Testing() => PublicApiCheck.Check<InMemoryResource>();
}
