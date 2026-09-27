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
                            EnableLocalFile = false,
                            EnableSpecificFile = false,
                            EnableEnvironment = false,
                            FileResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                );
            });
            var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();
            var value = await options.GetValueAsync();

            (value.RetryCount).ShouldBe(11);

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
                            EnableSpecificFile = false,
                            EnableEnvironment = false,
                            WriteLayer = CommonSourceWriteLayer.Local,
                            FileResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                );
            });
            var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();
            var value = await options.GetValueAsync();

            (value.RetryCount).ShouldBe(1);
            (value.Label).ShouldBe("yaml-local");
            (await options.ExplainAsync("Label")).HighestPrioritySourceId.ShouldBe("common.local");
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
                            EnableLocalFile = false,
                            EnableSpecificFile = false,
                            EnableEnvironment = false,
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
                            EnableGlobalFile = false,
                            EnableLocalFile = false,
                            SpecificFilePath = specificPath,
                            EnableEnvironment = false,
                            WriteLayer = CommonSourceWriteLayer.Specific,
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
