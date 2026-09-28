using System.Buffers;
using System.Text;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Source.Common;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class CommonSourceFormatTests
{
    [Test]
    public async Task CommonSources_UsesEnvironmentWithoutAnExplicitIdOrPriority()
    {
        using var directory = new TemporaryDirectory();
        var appId = $"Configlue.Tests.{Guid.NewGuid():N}";
        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.UseCommonSources(
                    new CommonSourceOptions
                    {
                        ApplicationId = appId,
                        GlobalFileName = "settings.json",
                        LocalFilePath = Path.Combine(directory.FullPath, "local.json"),
                        EnvironmentPrefix = "CONFIGLUE_TEST",
                        EnvironmentVariables = () =>
                            [new KeyValuePair<string, string?>("CONFIGLUE_TEST__RetryCount", "17")],
                    }
                )
            );
        });

        var options = context.GetAdvancedOptions<AppSettings>();
        (await options.GetValueAsync()).RetryCount.ShouldBe(17);
        string.IsNullOrWhiteSpace(
                options
                    .GetDiagnostics()
                    .Sources.Single(static source =>
                        source.PhysicalOrigin?.StartsWith("environment:") == true
                    )
                    .Id
            )
            .ShouldBeFalse();
    }

    [Test]
    public async Task CommonSources_InfersYamlFromGlobalFileName()
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

            await using var context = Configlue.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.UseCommonSources(
                        new CommonSourceOptions
                        {
                            ApplicationId = appId,
                            GlobalFileName = "settings.yaml",
                            WriteLayer = CommonSourceWriteLayer.Global,
                            FileResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                );
            });
            var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();
            var value = await options.GetValueAsync();

            (value.RetryCount).ShouldBe(11);
            options.GetDiagnostics().DefaultUsesHighestPriorityWritable.ShouldBeTrue();
            options.GetDiagnostics().Sources.All(static source => source.CanWrite).ShouldBeTrue();

            await options.SaveAsync(settings => settings.Label = "written-to-yaml");
            var written = await File.ReadAllTextAsync(globalPath);
            (written.Contains("written-to-yaml", StringComparison.Ordinal)).ShouldBeTrue();
            (written.Contains('{', StringComparison.Ordinal)).ShouldBeFalse();
        }
        finally
        {
            DeleteStandardFile(globalPath);
        }
    }

    [Test]
    public async Task CommonSources_SupportsMixedJsonAndYamlLayers()
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

            await using var context = Configlue.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.UseCommonSources(
                        new CommonSourceOptions
                        {
                            ApplicationId = appId,
                            GlobalFileName = "settings.json",
                            LocalFilePath = localPath,
                            FileResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                );
            });
            var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();
            var value = await options.GetValueAsync();

            (value.RetryCount).ShouldBe(1);
            (value.Label).ShouldBe("yaml-local");
            ((await options.GetDetailsAsync()).Label.Source?.Kind).ShouldBe("File");
        }
        finally
        {
            DeleteStandardFile(globalPath);
        }
    }

    [Test]
    public async Task CommonSources_HonorsExplicitFormatOverride()
    {
        using var directory = new TemporaryDirectory();
        var appId = $"Configlue.Tests.{Guid.NewGuid():N}";
        var globalPath = Path.Combine(
            ConfiglueStandardPaths.GetStandardSaveDirectory(appId),
            "settings.json"
        );
        try
        {
            await WriteYamlFragmentAsync(
                globalPath,
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(23) }
            );

            await using var context = Configlue.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.UseCommonSources(
                        new CommonSourceOptions
                        {
                            ApplicationId = appId,
                            GlobalFileName = "settings.json",
                            FileFormat = CommonSourceFileFormat.Yaml,
                            FileResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                );
            });
            var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();

            ((await options.GetValueAsync()).RetryCount).ShouldBe(23);
        }
        finally
        {
            DeleteStandardFile(globalPath);
        }
    }

    [Test]
    public async Task CommonSources_InfersXmlFromSpecificFileExtension()
    {
        using var directory = new TemporaryDirectory();
        var appId = $"Configlue.Tests.{Guid.NewGuid():N}";
        var specificPath = Path.Combine(directory.FullPath, "selected.xml");
        var globalPath = Path.Combine(
            ConfiglueStandardPaths.GetStandardSaveDirectory(appId),
            "settings.json"
        );
        try
        {
            await WriteXmlFragmentAsync(
                specificPath,
                new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) }
            );

            await using var context = Configlue.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.UseCommonSources(
                        new CommonSourceOptions
                        {
                            ApplicationId = appId,
                            GlobalFileName = "settings.json",
                            SpecificFilePath = specificPath,
                            FileResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                );
            });
            var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();

            ((await options.GetValueAsync()).Enabled).ShouldBeFalse();
        }
        finally
        {
            DeleteStandardFile(globalPath);
        }
    }

    [Test]
    public async Task CommonSources_NonDefaultLocalLayerRemainsWritableThroughItsSelector()
    {
        using var directory = new TemporaryDirectory();
        var appId = $"Configlue.Tests.{Guid.NewGuid():N}";
        var localPath = Path.Combine(directory.FullPath, "local.json");
        var specificPath = Path.Combine(directory.FullPath, "selected.json");
        var globalPath = Path.Combine(
            ConfiglueStandardPaths.GetStandardSaveDirectory(appId),
            "settings.json"
        );

        try
        {
            await using var context = Configlue.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.UseCommonSources(
                        new CommonSourceOptions
                        {
                            ApplicationId = appId,
                            GlobalFileName = "settings.json",
                            LocalFilePath = localPath,
                            SpecificFilePath = specificPath,
                            FileResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                );
            });
            var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();

            await options
                .Source(CommonSource.Local)
                .SaveAsync(
                    new AppSettings.Patch
                    {
                        Label = FragmentOperation<string?>.Set("explicit-local-write"),
                    }
                );

            var bytes = await File.ReadAllBytesAsync(localPath);
            var document = new ReadOnlySequence<byte>(bytes);
            var fragment = new JsonStateCodec<AppSettings.Fragment>().Deserialize(
                in document,
                default
            )!;
            (fragment.Label.Value).ShouldBe("explicit-local-write");
            options
                .GetDiagnostics()
                .Sources.Single(source => source.PhysicalOrigin == Path.GetFullPath(localPath))
                .CanWrite.ShouldBeTrue();
        }
        finally
        {
            DeleteStandardFile(globalPath);
        }
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
