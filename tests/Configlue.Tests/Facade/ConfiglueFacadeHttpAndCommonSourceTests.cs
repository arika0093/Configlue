using System.Buffers;
using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Configlue.Extensions.MSOptions;
using Configlue.Provider.Json;
using Configlue.Source.CommandLine;
using Configlue.Source.Presets;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed partial class ConfiglueFacadeSourceTests
{
    [Test]
    public async Task CommonSourcePresetWorksInDependencyInjection()
    {
        using var directory = new TemporaryDirectory();
        var selectedPath = Path.Combine(directory.FullPath, "selected.json");
        await WriteFragmentAsync(
            selectedPath,
            new AppSettings.Fragment { Label = Optional<string?>.Present("selected-di") }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglue(builder =>
        {
            builder.UseCommonSources(sources =>
            {
                sources
                    .WithExplicit(selectedPath)
                    .FileResourceOptions(new FileResourceOptions { CreateBackup = false });
                sources.Add<AppSettings>();
            });
        });

        await using var provider = services.BuildServiceProvider();
        (
            provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AppSettings>>()
                .CurrentValue.Label
        ).ShouldBe("selected-di");
    }

    [Test]
    public async Task CommonSourcePreset_OrdersSparseLayersAndRoutesWritesToSelectedFile()
    {
        using var directory = new TemporaryDirectory();
        var appId = $"Configlue.Tests.{Guid.NewGuid():N}";
        var globalPath = Path.Combine(
            ConfiglueStandardPaths.GetStandardSaveDirectory(appId),
            "settings.json"
        );
        var localPath = Path.Combine(directory.FullPath, "local.json");
        var specificPath = Path.Combine(directory.FullPath, "selected.json");
        try
        {
            await WriteFragmentAsync(
                globalPath,
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
            );
            await WriteFragmentAsync(
                localPath,
                new AppSettings.Fragment { Label = Optional<string?>.Present("local") }
            );
            await WriteFragmentAsync(
                specificPath,
                new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) }
            );

            var retryOption = new Option<int>("--retry");
            var settingsFileOption = new Option<string>("--settings");
            var rootCommand = new RootCommand();
            rootCommand.Options.Add(retryOption);
            rootCommand.Options.Add(settingsFileOption);
            var parseResult = rootCommand.Parse(["--settings", specificPath, "--retry", "5"]);
            await using var context = ConfiglueApp.CreateContext(builder =>
            {
                builder.UseCommonSources(sources =>
                {
                    sources
                        .WithEnvironment("CONFIGLUE_TEST")
                        .EnvironmentVariables(() =>
                            [new KeyValuePair<string, string?>("CONFIGLUE_TEST__RetryCount", "4")]
                        );
                    sources.WithCommandLine(
                        parseResult,
                        mappings => mappings.Map(retryOption, "RetryCount")
                    );
                    sources
                        .WithExplicit(specificPath)
                        .FileResourceOptions(new FileResourceOptions { CreateBackup = false });
                    sources
                        .WithUserGlobal(appId, "settings.json")
                        .FileResourceOptions(new FileResourceOptions { CreateBackup = false });
                    sources
                        .WithLocal(localPath)
                        .FileResourceOptions(new FileResourceOptions { CreateBackup = false });
                    sources.Add<AppSettings>();
                });
            });
            var options = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();
            var value = await options.GetValueAsync();
            (value.RetryCount).ShouldBe(5);
            (value.Label).ShouldBe("local");
            (value.Enabled).ShouldBeFalse();
            ((await options.GetDetailsAsync()).RetryCount.Source?.Kind).ShouldBe("CommandLine");

            await options.SaveAsync(settings => settings.Label = "written-to-specific");
            var written = new JsonStateCodec<AppSettings.Fragment>();
            var selected = await File.ReadAllBytesAsync(specificPath);
            var sequence = new ReadOnlySequence<byte>(selected);
            var selectedFragment = written.Deserialize(in sequence, default)!;
            (selectedFragment.Label.Value).ShouldBe("written-to-specific");
            await options
                .Source(CommonSource.Specific)
                .SaveAsync(
                    new AppSettings.Patch
                    {
                        Label = FragmentOperation<string?>.Set("explicit-selector-write"),
                    }
                );
            selected = await File.ReadAllBytesAsync(specificPath);
            sequence = new ReadOnlySequence<byte>(selected);
            selectedFragment = written.Deserialize(in sequence, default)!;
            (selectedFragment.Label.Value).ShouldBe("explicit-selector-write");
            await options
                .Source(CommonSource.UserGlobal)
                .SaveAsync(
                    new AppSettings.Patch
                    {
                        Label = FragmentOperation<string?>.Set("explicit-global-write"),
                    }
                );
            var globalBytes = await File.ReadAllBytesAsync(globalPath);
            var globalSequence = new ReadOnlySequence<byte>(globalBytes);
            var globalFragment = written.Deserialize(in globalSequence, default)!;
            (globalFragment.Label.Value).ShouldBe("explicit-global-write");
            await options
                .Source(CommonSource.Local)
                .SaveAsync(
                    new AppSettings.Patch
                    {
                        Label = FragmentOperation<string?>.Set("explicit-local-write"),
                    }
                );
            var localBytes = await File.ReadAllBytesAsync(localPath);
            var localSequence = new ReadOnlySequence<byte>(localBytes);
            var localFragment = written.Deserialize(in localSequence, default)!;
            (localFragment.Label.Value).ShouldBe("explicit-local-write");
            await Should.ThrowAsync<StateConflictException>(async () =>
                await options.SaveAsync(settings => settings.RetryCount = 8)
            );
        }
        finally
        {
            if (File.Exists(globalPath))
            {
                File.Delete(globalPath);
                var appDirectory = Path.GetDirectoryName(globalPath)!;
                if (!Directory.EnumerateFileSystemEntries(appDirectory).Any())
                {
                    Directory.Delete(appDirectory);
                }
            }
        }
    }

}
