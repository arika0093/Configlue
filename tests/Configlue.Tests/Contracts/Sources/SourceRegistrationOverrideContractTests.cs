using System.Buffers;
using Configlue.Provider.Json;
#if NET10_0_OR_GREATER
using Configlue.Provider.MessagePack;
#endif
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Source.Environment;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

/// <summary>
/// Shared contract tests for common source-registration overrides. Providers must not repeat
/// these behavioral assertions; provider tests cover genuinely provider-specific behavior.
/// </summary>
public sealed class SourceRegistrationOverrideContractTests
{
    [Test]
    public async Task SharedRegistration_AppliesCommonOverridesToAProviderSource()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        await WriteJsonFragmentAsync(
            path,
            new AppSettings.Fragment { Label = Optional<string?>.Present("configured") }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources
                        .FromJsonFile(
                            new JsonFileSourceOptions
                            {
                                Path = path,
                                WatchChanges = false,
                                ResourceOptions = new FileResourceOptions
                                {
                                    CreateBackup = false,
                                },
                            }
                        )
                        .Named("settings")
                        .Priority(25)
                        .FallbackWhen(StateFallbackCondition.NotFoundOrUnavailable)
                        .ExplicitOnly();
                })
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        (await state.GetValueAsync()).Label.ShouldBe("configured");
        var diagnostics = state.GetDiagnostics();
        var source = diagnostics.Sources.Single();
        source.Id.ShouldBe(SourceId.From("settings"));
        source.Priority.ShouldBe(25);
        source.FallbackCondition.ShouldBe(StateFallbackCondition.NotFoundOrUnavailable);
        source.CanWrite.ShouldBeTrue();
        source.PhysicalOrigin.ShouldBe(Path.GetFullPath(path));
        diagnostics.DefaultWriteSourceId.ShouldBeNull();
    }

    [Test]
    public async Task SharedRegistration_ReadOnlyDisablesWrites()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "defaults.json");
        await WriteJsonFragmentAsync(
            path,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources
                        .FromJsonFile(
                            new JsonFileSourceOptions
                            {
                                Path = path,
                                WatchChanges = false,
                                ResourceOptions = new FileResourceOptions
                                {
                                    CreateBackup = false,
                                },
                            }
                        )
                        .Named("defaults")
                        .ReadOnly();
                })
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(3);
        state.GetDiagnostics().Sources.Single().CanWrite.ShouldBeFalse();
    }

    [Test]
    public void SharedRegistration_WritableRequiresAProviderWriter()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        Directory.CreateDirectory(directory.FullPath);
        File.WriteAllText(path, "{}");

        Should.Throw<InvalidOperationException>(() =>
            ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        sources
                            .FromJsonFile(
                                new JsonFileSourceOptions
                                {
                                    Path = path,
                                    WatchChanges = false,
                                    ReadOnly = true,
                                }
                            )
                            .Writable();
                    })
                );
            })
        );
    }

    [Test]
    public async Task FluentJsonFileRegistration_DelegatesCommonOverridesToSharedRegistration()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        var readOnlyPath = Path.Combine(directory.FullPath, "defaults.json");
        await WriteJsonFragmentAsync(
            path,
            new AppSettings.Fragment { Label = Optional<string?>.Present("configured") }
        );
        await WriteJsonFragmentAsync(
            readOnlyPath,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources
                        .JsonFile(path)
                        .Named("settings")
                        .Priority(25)
                        .FallbackWhen(StateFallbackCondition.NotFoundOrUnavailable)
                        .Writable()
                        .ExplicitOnly()
                        .WatchChanges(false);
                    sources
                        .JsonFile(readOnlyPath)
                        .Named(SourceKey<AppSettings>.Named("defaults"))
                        .Priority(0)
                        .ReadOnly()
                        .WatchChanges(false);
                })
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        (await state.GetValueAsync()).Label.ShouldBe("configured");
        var diagnostics = state.GetDiagnostics();
        var source = diagnostics.Sources.Single(source =>
            source.Id == SourceId.From("settings")
        );
        var readOnly = diagnostics.Sources.Single(source =>
            source.Id == SourceId.From("defaults")
        );
        source.Priority.ShouldBe(25);
        source.FallbackCondition.ShouldBe(StateFallbackCondition.NotFoundOrUnavailable);
        source.CanWrite.ShouldBeTrue();
        source.CanWatch.ShouldBeFalse();
        readOnly.CanWrite.ShouldBeFalse();
        diagnostics.DefaultWriteSourceId.ShouldBeNull();
    }

    [Test]
    public async Task SharedRegistration_AppliesCommonOverridesAcrossProviders()
    {
        using var directory = new TemporaryDirectory();
        var yamlPath = Path.Combine(directory.FullPath, "settings.yaml");
        var xmlPath = Path.Combine(directory.FullPath, "settings.xml");
#if NET10_0_OR_GREATER
        var messagePackPath = Path.Combine(directory.FullPath, "settings.msgpack");
#endif
        await WriteYamlFragmentAsync(
            yamlPath,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(11) }
        );
        await WriteXmlFragmentAsync(
            xmlPath,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) }
        );
#if NET10_0_OR_GREATER
        await WriteMessagePackFragmentAsync(
            messagePackPath,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(13) }
        );
#endif

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources
                        .FromYamlFile(
                            new YamlFileSourceOptions
                            {
                                Path = yamlPath,
                                WatchChanges = false,
                                ResourceOptions = new FileResourceOptions
                                {
                                    CreateBackup = false,
                                },
                            }
                        )
                        .Named("yaml-settings")
                        .Priority(30)
                        .ExplicitOnly();
                    sources
                        .FromXmlFile(
                            new XmlFileSourceOptions
                            {
                                Path = xmlPath,
                                WatchChanges = false,
                                ResourceOptions = new FileResourceOptions
                                {
                                    CreateBackup = false,
                                },
                            }
                        )
                        .Named("xml-settings")
                        .Priority(20)
                        .ReadOnly();
#if NET10_0_OR_GREATER
                    sources
                        .FromMessagePackFile(
                            new MessagePackFileSourceOptions
                            {
                                Path = messagePackPath,
                                WatchChanges = false,
                                ResourceOptions = new FileResourceOptions
                                {
                                    CreateBackup = false,
                                },
                            }
                        )
                        .Named("messagepack-settings")
                        .Priority(10)
                        .FallbackWhen(StateFallbackCondition.NotFoundOrUnavailable);
#endif
                    sources
                        .FromEnvironment(
                            new EnvironmentSourceOptions
                            {
                                Prefix = "CONFIGLUE_CONTRACT_TEST",
                                EnvironmentVariables = () =>
                                    [
                                        new KeyValuePair<string, string?>(
                                            "CONFIGLUE_CONTRACT_TEST__RETRYCOUNT",
                                            "14"
                                        ),
                                    ],
                            }
                        )
                        .Named("environment-settings")
                        .Priority(40)
                        .ExplicitOnly();
                })
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(14);
        var sourcesById = state
            .GetDiagnostics()
            .Sources.ToDictionary(source => source.Id.Value);
        sourcesById["yaml-settings"].Priority.ShouldBe(30);
        sourcesById["yaml-settings"].CanWrite.ShouldBeTrue();
        sourcesById["yaml-settings"].PhysicalOrigin.ShouldBe(Path.GetFullPath(yamlPath));
        sourcesById["xml-settings"].Priority.ShouldBe(20);
        sourcesById["xml-settings"].CanWrite.ShouldBeFalse();
#if NET10_0_OR_GREATER
        sourcesById["messagepack-settings"].Priority.ShouldBe(10);
        sourcesById["messagepack-settings"]
            .FallbackCondition.ShouldBe(StateFallbackCondition.NotFoundOrUnavailable);
#endif
        sourcesById["environment-settings"].Priority.ShouldBe(40);
        sourcesById["environment-settings"].CanWrite.ShouldBeFalse();
    }

    [Test]
    public async Task SharedRegistration_PreservesPhysicalOriginAndResourceIdentity()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        await WriteJsonFragmentAsync(
            path,
            new AppSettings.Fragment { Label = Optional<string?>.Present("configured") }
        );
        var fixedResourceId = new ResourceId("contract:fixed");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources
                        .FromJsonFile(
                            new JsonFileSourceOptions
                            {
                                Path = path,
                                WatchChanges = false,
                                FixedResourceId = fixedResourceId,
                                ResourceOptions = new FileResourceOptions
                                {
                                    CreateBackup = false,
                                },
                            }
                        )
                        .Named("settings")
                        .Priority(25)
                        .FallbackWhen(StateFallbackCondition.NotFoundOrUnavailable)
                        .ExplicitOnly();
                })
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        (await state.GetValueAsync()).Label.ShouldBe("configured");
        var source = state.GetDiagnostics().Sources.Single();
        source.PhysicalOrigin.ShouldBe(Path.GetFullPath(path));
        source.FixedResourceId.ShouldBe(fixedResourceId);
    }

    [Test]
    public async Task SharedRegistration_ScopedRuntimeIsolatesStatePerScope()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        await WriteJsonFragmentAsync(
            path,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(7) }
        );

        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources
                        .FromJsonFile(
                            new JsonFileSourceOptions
                            {
                                Path = path,
                                WatchChanges = false,
                                ResourceOptions = new FileResourceOptions
                                {
                                    CreateBackup = false,
                                },
                            }
                        )
                        .ScopedRuntime();
                })
            );
        });
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();
        using var otherScope = provider.CreateScope();

        var state = scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>();
        var otherState = otherScope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(7);
        (await otherState.GetValueAsync()).RetryCount.ShouldBe(7);
        ReferenceEquals(state, otherState).ShouldBeFalse();
        ReferenceEquals(
                state,
                scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>()
            )
            .ShouldBeTrue();
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

#if NET10_0_OR_GREATER
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
#endif

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
