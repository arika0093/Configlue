using System.Buffers;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Provider.MessagePack;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Source.Presets;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed partial class CommonSourceFormatTests
{
    [Test]
    public async Task CommonSourceBuilder_UsesEnvironmentWithoutAnExplicitIdOrPriority()
    {
        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.UseCommonSources(sources =>
            {
                sources
                    .WithEnvironment("CONFIGLUE_TEST")
                    .EnvironmentVariables(() =>
                        [new KeyValuePair<string, string?>("CONFIGLUE_TEST__RetryCount", "17")]
                    );
                sources.Add<AppSettings>();
            })
        );

        var options = context.GetRuntimeState<AppSettings>();
        (await options.GetValueAsync()).RetryCount.ShouldBe(17);
        options.GetDiagnostics().Sources.Single().CanWrite.ShouldBeFalse();
    }

    [Test]
    public async Task CommonSourceBuilder_PreservesFixedLayerPrecedence()
    {
        using var directory = new TemporaryDirectory();
        var appId = $"Configlue.Tests.{Guid.NewGuid():N}";
        var globalPath = Path.Combine(
            ConfiglueStandardPaths.GetStandardSaveDirectory(appId),
            "settings.json"
        );
        var localPath = Path.Combine(directory.FullPath, "local.json");
        var explicitPath = Path.Combine(directory.FullPath, "explicit.json");
        try
        {
            await WriteJsonFragmentAsync(
                globalPath,
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
            );
            await WriteJsonFragmentAsync(
                localPath,
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
            );
            await WriteJsonFragmentAsync(
                explicitPath,
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
            );

            await using var context = ConfiglueApp.CreateContext(builder =>
                builder.UseCommonSources(sources =>
                {
                    sources
                        .WithEnvironment("CONFIGLUE_TEST")
                        .EnvironmentVariables(() =>
                            [new KeyValuePair<string, string?>("CONFIGLUE_TEST__RetryCount", "4")]
                        );
                    sources.WithExplicit(explicitPath);
                    sources.WithUserGlobal(appId);
                    sources.WithLocal(localPath);
                    sources.Add<AppSettings>();
                })
            );
            var options = context.GetRuntimeState<AppSettings>();

            (await options.GetValueAsync()).RetryCount.ShouldBe(4);
            ((await options.GetDetailsAsync()).RetryCount.Source?.Kind).ShouldBe("Environment");
        }
        finally
        {
            DeleteStandardFile(globalPath);
        }
    }

    [Test]
    public async Task CommonSourceBuilder_DefaultsOrdinaryWritesToLocalWhenConfigured()
    {
        using var directory = new TemporaryDirectory();
        var localPath = Path.Combine(directory.FullPath, "local.json");
        const string applicationId = "Configlue.Tests.WritePreference";
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.OverrideHostPath(ConfiglueStandardLocation.UserGlobal, _ => directory.FullPath);
            builder.UseCommonSources(sources =>
            {
                sources.WithUserGlobal(applicationId);
                sources.WithLocal(localPath);
                sources.Add<AppSettings>();
            });
        });
        var options = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();

        await options.SaveAsync(settings => settings.Label = "written-to-local");

        var bytes = await File.ReadAllBytesAsync(localPath);
        var sequence = new ReadOnlySequence<byte>(bytes);
        var fragment = new JsonStateCodec<AppSettings.Fragment>().Deserialize(
            in sequence,
            default
        )!;
        fragment.Label.Value.ShouldBe("written-to-local");
    }

    [Test]
    public async Task CommonSourceBuilder_SelectsYamlForGlobalFile()
    {
        var appId = $"Configlue.Tests.{Guid.NewGuid():N}";
        var globalPath = Path.Combine(
            ConfiglueStandardPaths.GetStandardSaveDirectory(appId),
            "settings.yaml"
        );
        try
        {
            await WriteYamlFragmentAsync(
                globalPath,
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(11) }
            );
            await using var context = ConfiglueApp.CreateContext(builder =>
                builder.UseCommonSources(sources =>
                {
                    sources
                        .WithUserGlobal(appId, "settings.yaml")
                        .Yaml()
                        .YamlSerializerOptions(new());
                    sources.Add<AppSettings>();
                })
            );
            var options = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();

            (await options.GetValueAsync()).RetryCount.ShouldBe(11);
            options.GetDiagnostics().DefaultWriteSourceIsInferred.ShouldBeFalse();
            await options.SaveAsync(settings => settings.Label = "written-to-yaml");
            var contents = await File.ReadAllTextAsync(globalPath);
            contents.ShouldContain("written-to-yaml");
            contents.ShouldNotContain("{");
        }
        finally
        {
            DeleteStandardFile(globalPath);
        }
    }

    [Test]
    public async Task CommonSourceBuilder_SupportsMixedJsonAndYamlLayers()
    {
        using var directory = new TemporaryDirectory();
        var appId = $"Configlue.Tests.{Guid.NewGuid():N}";
        var globalPath = Path.Combine(
            ConfiglueStandardPaths.GetStandardSaveDirectory(appId),
            "settings.json"
        );
        var localPath = Path.Combine(directory.FullPath, "local.yaml");
        try
        {
            await WriteJsonFragmentAsync(
                globalPath,
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
            );
            await WriteYamlFragmentAsync(
                localPath,
                new AppSettings.Fragment { Label = Optional<string?>.Present("yaml-local") }
            );
            await using var context = ConfiglueApp.CreateContext(builder =>
                builder.UseCommonSources(sources =>
                {
                    sources.WithUserGlobal(appId);
                    sources.WithLocal(localPath).Yaml();
                    sources.Add<AppSettings>();
                })
            );

            var value = await context.GetState<AppSettings>().GetValueAsync();
            value.RetryCount.ShouldBe(1);
            value.Label.ShouldBe("yaml-local");
        }
        finally
        {
            DeleteStandardFile(globalPath);
        }
    }

    [Test]
    public async Task CommonSourceBuilder_SelectsXmlProviderExplicitly()
    {
        using var directory = new TemporaryDirectory();
        var xmlPath = Path.Combine(directory.FullPath, "selected.xml");
        await WriteXmlFragmentAsync(
            xmlPath,
            new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.UseCommonSources(sources =>
            {
                sources.WithExplicit(xmlPath).Xml();
                sources.Add<AppSettings>();
            })
        );

        (await context.GetState<AppSettings>().GetValueAsync()).Enabled.ShouldBeFalse();
    }

    [Test]
    public async Task CommonSourceBuilder_SelectsMessagePackProviderExplicitly()
    {
        using var directory = new TemporaryDirectory();
        var messagePackPath = Path.Combine(directory.FullPath, "selected.msgpack");
        await WriteMessagePackFragmentAsync(
            messagePackPath,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(42) }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.UseCommonSources(sources =>
            {
                sources.WithExplicit(messagePackPath).MessagePack();
                sources.Add<AppSettings>();
            })
        );

        (await context.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(42);
    }

    [Test]
    public async Task CommonSourceBuilder_AllowsWritingToANonDefaultFileBySelector()
    {
        using var directory = new TemporaryDirectory();
        var localPath = Path.Combine(directory.FullPath, "local.json");
        var explicitPath = Path.Combine(directory.FullPath, "selected.json");
        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.UseCommonSources(sources =>
            {
                sources.WithLocal(localPath);
                sources.WithExplicit(explicitPath);
                sources.Add<AppSettings>();
            })
        );
        var options = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();

        await options
            .Source(CommonSource.Local)
            .SaveAsync(
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("local-write") }
            );

        var bytes = await File.ReadAllBytesAsync(localPath);
        var sequence = new ReadOnlySequence<byte>(bytes);
        var fragment = new JsonStateCodec<AppSettings.Fragment>().Deserialize(
            in sequence,
            default
        )!;
        fragment.Label.Value.ShouldBe("local-write");
        options
            .GetDiagnostics()
            .Sources.Single(source => source.PhysicalOrigin == Path.GetFullPath(localPath))
            .CanWrite.ShouldBeTrue();
    }

    private static async Task WriteJsonFragmentAsync(string path, AppSettings.Fragment fragment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var output = new ArrayBufferWriter<byte>();
        new JsonStateCodec<AppSettings.Fragment>().Serialize(fragment, output, default);
        await File.WriteAllBytesAsync(path, output.WrittenMemory.ToArray());
    }

    private static async Task WriteYamlFragmentAsync(string path, AppSettings.Fragment fragment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var output = new ArrayBufferWriter<byte>();
        new YamlStateCodec<AppSettings.Fragment>(modelSchema: AppSettings.FragmentSchema).Serialize(
            fragment,
            output,
            default
        );
        await File.WriteAllBytesAsync(path, output.WrittenMemory.ToArray());
    }

    private static async Task WriteMessagePackFragmentAsync(
        string path,
        AppSettings.Fragment fragment
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var output = new ArrayBufferWriter<byte>();
        new MessagePackStateCodec<AppSettings.Fragment>().Serialize(fragment, output, default);
        await File.WriteAllBytesAsync(path, output.WrittenMemory.ToArray());
    }

    private static async Task WriteXmlFragmentAsync(string path, AppSettings.Fragment fragment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var output = new ArrayBufferWriter<byte>();
        new XmlStateCodec<AppSettings.Fragment>().Serialize(fragment, output, default);
        await File.WriteAllBytesAsync(path, output.WrittenMemory.ToArray());
    }

    private static void DeleteStandardFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            var appDirectory = Path.GetDirectoryName(path)!;
            if (!Directory.EnumerateFileSystemEntries(appDirectory).Any())
            {
                Directory.Delete(appDirectory);
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() =>
            FullPath = Path.Combine(
                Path.GetTempPath(),
                "Configlue.Tests",
                Guid.NewGuid().ToString("N")
            );

        public string FullPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(FullPath))
            {
                Directory.Delete(FullPath, recursive: true);
            }
        }
    }
}
