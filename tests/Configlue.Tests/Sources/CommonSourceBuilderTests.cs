using System.CommandLine;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Source.CommandLine;
using Configlue.Source.Presets;
using Configlue.Testing;
using Configlue.Transformer.AES;

namespace Configlue.Tests;

public sealed partial class CommonSourceFormatTests
{
    [Test]
    public async Task CommonSourceBuilder_UsesFixedLayerPriorityAndAllowsCustomInsertion()
    {
        using var directory = new TemporaryDirectory();
        var applicationId = $"Configlue.Tests.{Guid.NewGuid():N}";
        var globalPath = Path.Combine(
            ConfiglueStandardPaths.GetStandardSaveDirectory(applicationId),
            "global.json"
        );
        var customPath = Path.Combine(directory.FullPath, "custom.json");
        var localPath = Path.Combine(directory.FullPath, "local.json");
        await WriteJsonFragmentAsync(
            globalPath,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await WriteJsonFragmentAsync(
            customPath,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
        );
        await WriteJsonFragmentAsync(
            localPath,
            new AppSettings.Fragment { Label = Optional<string?>.Present("local") }
        );

        try
        {
            await using var context = ConfiglueApp.CreateContext(configure =>
                configure.UseCommonSources(sources =>
                {
                    sources.WithLocal(localPath);
                    sources.WithCustom(
                        CommonSourceLayer.Global,
                        builder =>
                            builder.FromJsonFile(
                                new JsonFileSourceOptions { Id = "custom", Path = customPath }
                            )
                    );
                    sources.WithGlobal(applicationId, "global.json");
                    sources.Add<AppSettings>();
                })
            );
            var options = context.GetAdvancedOptions<AppSettings>();

            (await options.GetValueAsync()).RetryCount.ShouldBe(2);
            options
                .GetDiagnostics()
                .Sources.Select(static source => source.Priority)
                .ShouldBe([100, 1, 0]);
        }
        finally
        {
            DeleteStandardFile(globalPath);
        }
    }

    [Test]
    public async Task CommonSourceBuilder_SelectsYamlAndOnlyRegistersDeclaredLayers()
    {
        using var directory = new TemporaryDirectory();
        var yamlPath = Path.Combine(directory.FullPath, "settings.data");
        await WriteYamlFragmentAsync(
            yamlPath,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(23) }
        );

        await using var context = ConfiglueApp.CreateContext(configure =>
            configure.UseCommonSources(sources =>
            {
                sources.WithExplicit(yamlPath).Yaml();
                sources.Add<AppSettings>();
            })
        );
        var options = context.GetAdvancedOptions<AppSettings>();

        (await options.GetValueAsync()).RetryCount.ShouldBe(23);
        var diagnostics = options.GetDiagnostics().Sources;
        diagnostics.Count.ShouldBe(1);
        diagnostics[0].Priority.ShouldBe(200);
        diagnostics[0].CanWrite.ShouldBeTrue();
    }

    [Test]
    public async Task CommonSourceBuilder_RegistersCommandLineAsReadOnlyAtItsFixedPriority()
    {
        using var directory = new TemporaryDirectory();
        var explicitPath = Path.Combine(directory.FullPath, "settings.json");
        await WriteJsonFragmentAsync(explicitPath, new AppSettings.Fragment());
        var retryOption = new Option<int>("--retry");
        var rootCommand = new RootCommand();
        rootCommand.Options.Add(retryOption);
        var parseResult = rootCommand.Parse(["--retry", "31"]);

        await using var context = ConfiglueApp.CreateContext(configure =>
            configure.UseCommonSources(sources =>
            {
                sources.WithCommandLine(
                    parseResult,
                    mappings => mappings.Map(retryOption, "RetryCount")
                );
                sources.WithExplicit(explicitPath);
                sources.Add<AppSettings>();
            })
        );
        var options = context.GetAdvancedOptions<AppSettings>();

        (await options.GetValueAsync()).RetryCount.ShouldBe(31);
        var commandLine = options
            .GetDiagnostics()
            .Sources.Single(static source => source.Priority == 400);
        commandLine.CanWrite.ShouldBeFalse();
    }

    [Test]
    public async Task CommonSourceBuilder_AppliesProviderByteTransformers()
    {
        using var directory = new TemporaryDirectory();
        var explicitPath = Path.Combine(directory.FullPath, "settings.json");
        await WriteJsonFragmentAsync(
            explicitPath,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(37) }
        );
        using var transformer = new AesGcmStateByteTransformer(new byte[32]);
        var plaintext = await File.ReadAllBytesAsync(explicitPath);
        var encrypted = transformer.TransformWrite(plaintext);
        await File.WriteAllBytesAsync(explicitPath, encrypted.ToArray());

        await using var context = ConfiglueApp.CreateContext(configure =>
            configure.UseCommonSources(sources =>
            {
                sources.WithExplicit(explicitPath).Transformer(transformer);
                sources.Add<AppSettings>();
            })
        );

        var options = context.GetOptions<AppSettings>();
        (await options.GetValueAsync()).RetryCount.ShouldBe(37);
        await options.SaveAsync(settings => settings.RetryCount = 39);
        (await options.GetValueAsync()).RetryCount.ShouldBe(39);
        (await File.ReadAllBytesAsync(explicitPath)).SequenceEqual(plaintext).ShouldBeFalse();
    }
}
