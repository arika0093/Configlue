using System.Buffers;
using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Configlue.Extensions.MSOptions;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Resource.Http;
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
    public async Task HttpFacadeSourceReportsEndpointIdentityAndHonorsOverride()
    {
        var endpoint = "https://settings.example.test/app-settings/";
        var defaultId = await WriteHttpPatchAndGetResourceIdAsync(endpoint, resourceId: null);
        using var client = new HttpClient(new NoContentHttpHandler());
        var expected = new HttpResourceReader(client, new Uri(endpoint)).ResourceId;
        (defaultId).ShouldBe(expected);

        var overrideId = new ResourceId("test-resource:settings");
        var actualOverride = await WriteHttpPatchAndGetResourceIdAsync(endpoint, overrideId);
        (actualOverride).ShouldBe(overrideId);
    }

    [Test]
    public async Task JsonHttpSourceUsesNamedClientAndDefaultsToReadOnly()
    {
        var requestedUris = new System.Collections.Concurrent.ConcurrentBag<Uri>();
        var content = SerializeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(11) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services
            .AddHttpClient("json-settings")
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpResponseHandler(requestedUris, content)
            );
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonHttpClientFactory(
                        "json-settings",
                        new JsonHttpSourceOptions
                        {
                            Id = "json-http",
                            EndPoint = "https://settings.example.test/json/",
                            WatchChanges = false,
                        }
                    )
                )
            );
        });

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IConfiglueOptions<AppSettings>>();
        (await options.GetValueAsync()).RetryCount.ShouldBe(11);
        requestedUris.ShouldContain(new Uri("https://settings.example.test/json/get"));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await options.OpenEditSessionAsync()
        );
    }

    [Test]
    public async Task JsonHttpSourceCanWriteWhenEnabledAndDoesNotOwnTheClient()
    {
        var handler = new RecordingHttpHandler(SerializeFragment(new AppSettings.Fragment()));
        using var client = new HttpClient(handler);
        await using (
            var context = ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                        sources.FromJsonHttp(
                            new JsonHttpSourceOptions
                            {
                                Id = "writable-json-http",
                                EndPoint = "https://settings.example.test/writable/",
                                Client = client,
                                Writable = true,
                                WatchChanges = false,
                            }
                        )
                    )
                );
            })
        )
        {
            var result = await (
                (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>()
            ).ApplyPatchesAsync([
                new StateSourcePatch(
                    "writable-json-http",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(3) }
                ),
            ]);
            (result.PhysicalWriteCount).ShouldBe(1);
            handler.RequestMethods.ShouldContain(HttpMethod.Put);
        }

        handler.IsDisposed.ShouldBeFalse();
        var response = await client.GetAsync("https://settings.example.test/after-dispose");
        (response.StatusCode).ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task HttpSourceUsesNamedFactoryClientsAndKeepsReadOnlyCapabilities()
    {
        var requestedUris = new System.Collections.Concurrent.ConcurrentBag<Uri>();
        var firstBody = SerializeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) }
        );
        var secondBody = SerializeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services
            .AddHttpClient("settings-primary")
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpResponseHandler(requestedUris, firstBody)
            );
        services
            .AddHttpClient("settings-secondary")
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpResponseHandler(requestedUris, secondBody)
            );
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromHttpClientFactory(
                        "settings-primary",
                        new HttpSourceOptions
                        {
                            Id = "primary",
                            EndPoint = "https://settings.example.test/primary/",
                            Codec = new JsonStateCodec<AppSettings.Fragment>(),
                        }
                    );
                    sources.FromHttpClientFactory(
                        "settings-secondary",
                        new HttpSourceOptions
                        {
                            Id = "secondary",
                            EndPoint = "https://settings.example.test/secondary/",
                            Codec = new JsonStateCodec<AppSettings.Fragment>(),
                            Priority = 10,
                        }
                    );
                })
            );
        });

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IConfiglueOptions<AppSettings>>();
        (await options.GetValueAsync()).RetryCount.ShouldBe(9);
        requestedUris.ShouldContain(new Uri("https://settings.example.test/primary/get"));
        requestedUris.ShouldContain(new Uri("https://settings.example.test/secondary/get"));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await options.OpenEditSessionAsync();
        });
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
                        .WithGlobal(appId, "settings.json")
                        .FileResourceOptions(new FileResourceOptions { CreateBackup = false });
                    sources
                        .WithLocal(localPath)
                        .FileResourceOptions(new FileResourceOptions { CreateBackup = false });
                    sources.Add<AppSettings>();
                });
            });
            var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();
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
                .Source(CommonSource.Global)
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
