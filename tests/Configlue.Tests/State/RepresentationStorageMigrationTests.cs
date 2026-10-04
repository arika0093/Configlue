using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Source.Environment;
using Configlue.Testing;

namespace Configlue.Tests;

/// <summary>
/// First-class representation/storage migration (#259): migration-only sources reuse the same
/// provider registration path as active sources without participating in resolution, and legacy
/// adoption/same-resource replacement run as explicit migration operations.
/// </summary>
public sealed class RepresentationStorageMigrationTests
{
    [Test]
    public async Task JsonToYaml_MigratesThroughGenericFragmentPipeline()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.yaml");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");
        // Seed via a throwaway active context so the legacy bytes are genuinely YAML.
        await SeedYamlAsync(legacyPath, retryCount: 7, label: "from-yaml");
        var legacyBefore = await File.ReadAllTextAsync(legacyPath);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromJsonFile(JsonOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromYamlFile(YamlOptions("legacy", legacyPath))
                    );
                });
            });
        });

        var migration = await context
            .GetSources<AppSettings>()
            .MigrateSourceAsync(SourceId.From("legacy"), SourceId.From("canonical"));

        var canonicalText = await File.ReadAllTextAsync(canonicalPath);
        var value = await context.GetState<AppSettings>().GetValueAsync();

        (migration.SourceId).ShouldBe(SourceId.From("legacy"));
        (migration.TargetId).ShouldBe(SourceId.From("canonical"));
        (value.RetryCount).ShouldBe(7);
        (value.Label).ShouldBe("from-yaml");
        canonicalText.ShouldContain("{");
        (await File.ReadAllTextAsync(legacyPath)).ShouldBe(legacyBefore);
    }

    [Test]
    public async Task YamlToJson_AdoptsWhenCanonicalIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.yaml");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");
        await SeedYamlAsync(legacyPath, retryCount: 11, label: "adopted");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromJsonFile(JsonOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromYamlFile(YamlOptions("legacy", legacyPath))
                    );
                });
            });
        });

        var missing = await context
            .GetSources<AppSettings>()
            .AdoptLegacyAsync(SourceId.From("canonical"), [SourceId.From("legacy")]);

        (missing).ShouldNotBeNull();
        (missing!.Value.SourceId).ShouldBe(SourceId.From("legacy"));
        (missing.Value.TargetId).ShouldBe(SourceId.From("canonical"));
        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(11);
        (value.Label).ShouldBe("adopted");
        File.Exists(canonicalPath).ShouldBeTrue();

        // A second adoption is a no-op once canonical state exists.
        var repeated = await context
            .GetSources<AppSettings>()
            .AdoptLegacyAsync(SourceId.From("canonical"), [SourceId.From("legacy")]);
        (repeated).ShouldBeNull();
    }

    [Test]
    public async Task AdoptLegacy_DoesNotOverwritePresentCanonical()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.yaml");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");
        await SeedYamlAsync(legacyPath, retryCount: 1, label: "legacy");
        await SeedJsonAsync(canonicalPath, retryCount: 9, label: "canonical");
        var canonicalBefore = await File.ReadAllBytesAsync(canonicalPath);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromJsonFile(JsonOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromYamlFile(YamlOptions("legacy", legacyPath))
                    );
                });
            });
        });

        var adopted = await context
            .GetSources<AppSettings>()
            .AdoptLegacyAsync(SourceId.From("canonical"), [SourceId.From("legacy")]);

        (adopted).ShouldBeNull();
        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(9);
        (value.Label).ShouldBe("canonical");
        (await File.ReadAllBytesAsync(canonicalPath)).ShouldBe(canonicalBefore);
    }

    [Test]
    public async Task AdoptLegacy_ProbesRepresentationsInOrder()
    {
        using var directory = new TemporaryDirectory();
        var missingPath = Path.Combine(directory.FullPath, "absent.yaml");
        var legacyPath = Path.Combine(directory.FullPath, "legacy.yaml");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");
        await SeedYamlAsync(legacyPath, retryCount: 5, label: "second");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromJsonFile(JsonOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                    {
                        sources.FromYamlFile(YamlOptions("absent", missingPath));
                        sources.FromYamlFile(YamlOptions("legacy", legacyPath));
                    });
                });
            });
        });

        var adopted = await context
            .GetSources<AppSettings>()
            .AdoptLegacyAsync(
                SourceId.From("canonical"),
                [SourceId.From("absent"), SourceId.From("legacy")]
            );

        (adopted).ShouldNotBeNull();
        (adopted!.Value.SourceId).ShouldBe(SourceId.From("legacy"));
        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(5);
        (value.Label).ShouldBe("second");
    }

    [Test]
    public async Task MigrationOnlySource_IsInvisibleToResolutionDiagnosticsAndWrites()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.yaml");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");
        await SeedYamlAsync(legacyPath, retryCount: 1, label: "legacy-shadow");
        await SeedJsonAsync(canonicalPath, retryCount: 9, label: "canonical");
        var legacyBefore = await File.ReadAllBytesAsync(legacyPath);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromJsonFile(JsonOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromYamlFile(YamlOptions("legacy", legacyPath))
                    );
                });
            });
        });

        // Normal resolution never merges the migration-only representation.
        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(9);
        (value.Label).ShouldBe("canonical");

        var diagnostics = context.GetDiagnostics<AppSettings>().GetDiagnostics();
        diagnostics
            .Sources.Select(static source => source.Id)
            .ShouldBe([SourceId.From("canonical")]);

        // Inferred ordinary writes go to the canonical source only.
        await context.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 10);
        var afterWrite = await context.GetState<AppSettings>().GetValueAsync();
        (afterWrite.RetryCount).ShouldBe(10);
        (afterWrite.Label).ShouldBe("canonical");
        (await File.ReadAllBytesAsync(legacyPath)).ShouldBe(legacyBefore);
    }

    [Test]
    public async Task JsonToXml_MigratesThroughGenericFragmentPipeline()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.json");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.xml");
        await SeedJsonAsync(legacyPath, retryCount: 7, label: "from-json");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromXmlFile(XmlOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromJsonFile(JsonOptions("legacy", legacyPath))
                    );
                });
            });
        });

        await context
            .GetSources<AppSettings>()
            .MigrateSourceAsync(SourceId.From("legacy"), SourceId.From("canonical"));

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(7);
        (value.Label).ShouldBe("from-json");
        var canonicalText = await File.ReadAllTextAsync(canonicalPath);
        canonicalText.ShouldContain("<");
        canonicalText.ShouldNotContain("{");
    }

    [Test]
    public async Task XmlToJson_MigratesThroughGenericFragmentPipeline()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.xml");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");
        await SeedXmlAsync(legacyPath, retryCount: 3, label: "from-xml");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromJsonFile(JsonOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromXmlFile(XmlOptions("legacy", legacyPath))
                    );
                });
            });
        });

        await context
            .GetSources<AppSettings>()
            .MigrateSourceAsync(SourceId.From("legacy"), SourceId.From("canonical"));

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(3);
        (value.Label).ShouldBe("from-xml");
        (await File.ReadAllTextAsync(canonicalPath)).ShouldContain("{");
    }

    [Test]
    public async Task HistoricalSchemaAndFormat_ComposeInOneMigration()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.yaml");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");
        await SeedHistoricalV1YamlAsync(legacyPath, retryCount: 7);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<HistoricalSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Id = "canonical",
                            Path = canonicalPath,
                            WatchChanges = false,
                            DocumentLayout = new DocumentLayoutOptions
                            {
                                ModelId = "historical-settings",
                            },
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromYamlFile(
                            new YamlFileSourceOptions
                            {
                                Id = "legacy",
                                Path = legacyPath,
                                WatchChanges = false,
                                DocumentLayout = new DocumentLayoutOptions
                                {
                                    ModelId = "historical-settings",
                                },
                                ResourceOptions = new FileResourceOptions { CreateBackup = false },
                            }
                        )
                    );
                });
                model.AddMigration<HistoricalSettings.Fragment>(new HistoricalV1ToV3Migration());
            });
        });

        await context
            .GetSources<HistoricalSettings>()
            .MigrateSourceAsync(SourceId.From("legacy"), SourceId.From("canonical"));

        await using var canonical = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<HistoricalSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Id = "canonical",
                            Path = canonicalPath,
                            WatchChanges = false,
                            DocumentLayout = new DocumentLayoutOptions
                            {
                                ModelId = "historical-settings",
                            },
                        }
                    )
                )
            );
        });
        var value = await canonical.GetState<HistoricalSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(7);
        (value.NewName).ShouldBe("migrated-from-v1");
        (await File.ReadAllTextAsync(canonicalPath)).ShouldContain("$version");
    }

    [Test]
    public async Task MigrationJournal_RetriesAfterPartialCompletion()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.yaml");
        var firstPath = Path.Combine(directory.FullPath, "first.json");
        var secondPath = Path.Combine(directory.FullPath, "second.json");
        await SeedYamlAsync(legacyPath, retryCount: 4, label: "journaled");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                {
                    sources.FromJsonFile(JsonOptions("first", firstPath));
                    sources.FromJsonFile(JsonOptions("second", secondPath));
                });
                model.Writes(write => write.DefaultTo(SourceKey<AppSettings>.Named("first")));
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromYamlFile(YamlOptions("legacy", legacyPath))
                    );
                });
            });
        });

        var definition = new StateStorageMigrationDefinition<AppSettings.Fragment>(
            "representation-retry",
            [SourceId.From("legacy")],
            [
                new StateStorageMigrationTarget<AppSettings.Fragment>(
                    SourceId.From("first"),
                    static fragment => fragment
                ),
                new StateStorageMigrationTarget<AppSettings.Fragment>(
                    SourceId.From("second"),
                    static fragment => fragment
                ),
            ]
        );
        var sources = context.GetSources<AppSettings>();

        // A journal failure interrupts the run, but the verified first target survives.
        var failingJournal = new FlakyMigrationJournal(failOnWriteCount: 1);
        await Should.ThrowAsync<IOException>(async () =>
            await sources.MigrateAsync(definition, failingJournal)
        );

        // Retrying with a working journal completes the remaining target without rework.
        var journal = new InMemoryMigrationJournal();
        var progress = await sources.MigrateAsync(definition, journal);
        (progress.CompletedTargetSourceIds.Count).ShouldBe(2);
        (progress.SourcesRetired).ShouldBeFalse();

        var first = await ReadJsonFileAsync(firstPath);
        var second = await ReadJsonFileAsync(secondPath);
        (first.RetryCount.Value).ShouldBe(4);
        (second.RetryCount.Value).ShouldBe(4);

        var idempotent = await sources.MigrateSourcesToTargetsAsync(
            [SourceId.From("legacy")],
            new Dictionary<SourceId, Func<IConfiglueFragment, IConfiglueFragment>>
            {
                [SourceId.From("first")] = static fragment => fragment,
                [SourceId.From("second")] = static fragment => fragment,
            }
        );
        (idempotent.Targets.All(static target => target.WasAlreadyCurrent)).ShouldBeTrue();
    }

    [Test]
    public async Task SourceChangeDuringMigration_ProducesConflict()
    {
        var flipping = new FlippingStateReader(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) },
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
        );
        var targetStore = new InMemoryStateSource<AppSettings.Fragment>();
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "flip",
                    flipping,
                    new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }
                ),
                new StateSource<AppSettings.Fragment>(
                    "target",
                    targetStore,
                    new StateSourceOptions<AppSettings.Fragment>
                    {
                        Priority = 0,
                        Writer = targetStore,
                    }
                ),
            ])
        );

        await Should.ThrowAsync<StateConflictException>(async () =>
            await runtime.MigrateSourcesToTargetsAsync(
                [SourceId.From("flip")],
                new Dictionary<SourceId, Func<AppSettings.Fragment, AppSettings.Fragment>>
                {
                    [SourceId.From("target")] = static fragment => fragment,
                }
            )
        );

        ((await targetStore.ReadAsync()).Status).ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task SameResource_CodecSwap_ReplacesInPlaceWithOriginalRevision()
    {
        // In-place representation replacement replaces the whole physical resource: the old
        // codec reads the old bytes plus the physical revision, the new codec serializes the
        // migrated fragment, and the replace is conditional on the original revision.
        // Section-scoped file helpers preserve siblings by design, so this path uses the
        // canonical SerializedSource composition directly over one shared file.
        using var directory = new TemporaryDirectory();
        var sharedPath = Path.Combine(directory.FullPath, "settings.config");
        var layout = new DocumentLayoutOptions { ModelId = "app-settings" };
        using var file = new FileResource(sharedPath);
        var yamlCodec = new YamlStateCodec<AppSettings.Fragment>(
            modelSchema: AppSettings.FragmentSchema,
            documentLayout: layout
        );
        var jsonCodec = new JsonStateCodec<AppSettings.Fragment>(documentLayout: layout);
        var legacySerialized = new SerializedSource<AppSettings.Fragment>(
            file,
            yamlCodec,
            writer: file,
            watcher: file
        );
        var legacy = new StateSource<AppSettings.Fragment>(
            "legacy-yaml",
            legacySerialized,
            new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }
        );
        var canonicalSerialized = new SerializedSource<AppSettings.Fragment>(
            file,
            jsonCodec,
            writer: file,
            watcher: file
        );
        var canonical = new StateSource<AppSettings.Fragment>(
            "canonical-json",
            canonicalSerialized,
            new StateSourceOptions<AppSettings.Fragment> { Priority = 0 }
        );
        await legacy.Writer!.WriteAsync(
            new StateWriteRequest<AppSettings.Fragment>(
                new AppSettings.Fragment
                {
                    RetryCount = Optional<int>.Present(6),
                    Label = Optional<string?>.Present("swapped"),
                }
            )
        );
        var yamlText = await File.ReadAllTextAsync(sharedPath);
        yamlText.ShouldNotContain("{");

        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([canonical]),
            StateWritePlan.DefaultTo(SourceId.From("canonical-json")),
            migrationSources: new StateSourceSet<AppSettings.Fragment>([legacy])
        );

        var migration = await runtime.MigrateSourceAsync(
            SourceId.From("legacy-yaml"),
            SourceId.From("canonical-json")
        );

        (migration.SourceId).ShouldBe(SourceId.From("legacy-yaml"));
        (migration.TargetId).ShouldBe(SourceId.From("canonical-json"));
        var text = await File.ReadAllTextAsync(sharedPath);
        text.ShouldContain("{");
        var value = await runtime.ReadAsync();
        (value.Value!.RetryCount).ShouldBe(6);
        (value.Value.Label).ShouldBe("swapped");

        // The migrated JSON document round-trips through the new codec only.
        var reread = await canonical.ReadAsync();
        (reread.Status).ShouldBe(StateReadStatus.Success);
        (reread.Value!.RetryCount.Value).ShouldBe(6);
    }

    [Test]
    public async Task CrossBackend_FileToMemory_MigratesWithoutProviderCoupling()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.FullPath, "source.json");
        await File.WriteAllTextAsync(
            sourcePath,
            """{"$version":2,"RetryCount":8,"Label":"cross-backend"}"""
        );
        using var sourceFile = new FileResource(sourcePath);
        var sourceSerialized = new SerializedSource<AppSettings.Fragment>(
            sourceFile,
            new JsonStateCodec<AppSettings.Fragment>(
                documentLayout: new DocumentLayoutOptions { ModelId = "app-settings" }
            ),
            writer: sourceFile,
            watcher: sourceFile
        );
        var source = new StateSource<AppSettings.Fragment>(
            "json-file",
            sourceSerialized,
            new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }
        );
        var memory = new InMemoryResource();
        var memoryCodec = new YamlStateCodec<AppSettings.Fragment>(
            modelSchema: AppSettings.FragmentSchema,
            documentLayout: new DocumentLayoutOptions { ModelId = "app-settings" }
        );
        var memorySerialized = new SerializedSource<AppSettings.Fragment>(
            memory,
            memoryCodec,
            writer: memory,
            watcher: memory
        );
        var memorySource = new StateSource<AppSettings.Fragment>(
            "memory-yaml",
            memorySerialized,
            new StateSourceOptions<AppSettings.Fragment> { Priority = 0 }
        );
        var sourceBefore = (await sourceFile.ReadAsync()).Content.ToArray();
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([memorySource]),
            StateWritePlan.DefaultTo(SourceId.From("memory-yaml")),
            migrationSources: new StateSourceSet<AppSettings.Fragment>([source])
        );

        // The file backend input is invisible to the active (memory-only) resolution.
        var before = await runtime.ReadAsync();
        (before.Value!.RetryCount).ShouldBe(3);

        var migration = await runtime.MigrateSourceAsync(
            SourceId.From("json-file"),
            SourceId.From("memory-yaml")
        );

        (migration.SourceId).ShouldBe(SourceId.From("json-file"));
        var after = await runtime.ReadAsync();
        (after.Value!.RetryCount).ShouldBe(8);
        (after.Value.Label).ShouldBe("cross-backend");
        (await sourceFile.ReadAsync()).Content.ToArray().ShouldBe(sourceBefore);
    }

    [Test]
    public async Task ReadOnlySource_AsMigrationInput()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.json");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");
        await SeedJsonAsync(legacyPath, retryCount: 2, label: "readonly");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromJsonFile(JsonOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromJsonFile(
                            new JsonFileSourceOptions
                            {
                                Id = "legacy",
                                Path = legacyPath,
                                ReadOnly = true,
                                WatchChanges = false,
                                ResourceOptions = new FileResourceOptions { CreateBackup = false },
                            }
                        )
                    );
                });
            });
        });

        await context
            .GetSources<AppSettings>()
            .MigrateSourceAsync(SourceId.From("legacy"), SourceId.From("canonical"));

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(2);
        (value.Label).ShouldBe("readonly");
    }

    [Test]
    public async Task ReadOnlyEnvironment_AsMigrationInput()
    {
        using var directory = new TemporaryDirectory();
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromJsonFile(JsonOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromEnvironment(
                            new EnvironmentSourceOptions
                            {
                                Id = "legacy-env",
                                Prefix = "LEGACY_MIGRATION",
                                EnvironmentVariables = () =>
                                    new Dictionary<string, string?>(StringComparer.Ordinal)
                                    {
                                        ["LEGACY_MIGRATION__RETRYCOUNT"] = "14",
                                        ["LEGACY_MIGRATION__LABEL"] = "from-env",
                                    },
                            }
                        )
                    );
                });
            });
        });

        await context
            .GetSources<AppSettings>()
            .MigrateSourceAsync(SourceId.From("legacy-env"), SourceId.From("canonical"));

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(14);
        (value.Label).ShouldBe("from-env");
    }

    [Test]
    public async Task NonWritableTarget_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.yaml");
        var targetPath = Path.Combine(directory.FullPath, "target.json");
        await SeedYamlAsync(legacyPath, retryCount: 2, label: "legacy");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                {
                    sources.FromJsonFile(JsonOptions("canonical", targetPath));
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Id = "readonly-target",
                            Path = Path.Combine(directory.FullPath, "readonly.json"),
                            ReadOnly = true,
                            WatchChanges = false,
                        }
                    );
                });
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromYamlFile(YamlOptions("legacy", legacyPath))
                    );
                });
            });
        });
        var sources = context.GetSources<AppSettings>();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await sources.MigrateSourceAsync(
                SourceId.From("legacy"),
                SourceId.From("readonly-target")
            )
        );
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await sources.MigrateSourcesToTargetsAsync(
                [SourceId.From("legacy")],
                new Dictionary<SourceId, Func<IConfiglueFragment, IConfiglueFragment>>
                {
                    [SourceId.From("readonly-target")] = static fragment => fragment,
                }
            )
        );
    }

    [Test]
    public async Task UnknownMigrationSource_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(JsonOptions("canonical", canonicalPath))
                )
            );
        });
        var sources = context.GetSources<AppSettings>();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await sources.MigrateSourceAsync(SourceId.From("missing"), SourceId.From("canonical"))
        );
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await sources.AdoptLegacyAsync(SourceId.From("canonical"), [SourceId.From("missing")])
        );
    }

    [Test]
    public async Task RetiringMigrationOnlySource_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.yaml");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");
        await SeedYamlAsync(legacyPath, retryCount: 2, label: "legacy");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromJsonFile(JsonOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromYamlFile(YamlOptions("legacy", legacyPath))
                    );
                });
            });
        });

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await context
                .GetSources<AppSettings>()
                .MigrateSourcesToTargetsAsync(
                    [SourceId.From("legacy")],
                    new Dictionary<SourceId, Func<IConfiglueFragment, IConfiglueFragment>>
                    {
                        [SourceId.From("canonical")] = static fragment => fragment,
                    },
                    retireSources: true
                )
        );
    }

    [Test]
    public async Task TargetProjection_SelectsMigratedMembers()
    {
        using var directory = new TemporaryDirectory();
        var legacyPath = Path.Combine(directory.FullPath, "legacy.yaml");
        var canonicalPath = Path.Combine(directory.FullPath, "canonical.json");
        await SeedYamlAsync(legacyPath, retryCount: 7, label: "projected-away");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromJsonFile(JsonOptions("canonical", canonicalPath))
                );
                model.StorageMigrations(migrations =>
                {
                    migrations.From(sources =>
                        sources.FromYamlFile(YamlOptions("legacy", legacyPath))
                    );
                });
            });
        });

        await context
            .GetSources<AppSettings>()
            .MigrateSourcesToTargetsAsync(
                [SourceId.From("legacy")],
                new Dictionary<SourceId, Func<IConfiglueFragment, IConfiglueFragment>>
                {
                    [SourceId.From("canonical")] = static fragment => new AppSettings.Fragment
                    {
                        RetryCount = ((AppSettings.Fragment)fragment).RetryCount,
                    },
                }
            );

        var value = await context.GetState<AppSettings>().GetValueAsync();
        (value.RetryCount).ShouldBe(7);
        (value.Label).ShouldBe("default");
    }

    private static JsonFileSourceOptions JsonOptions(string id, string path) =>
        new()
        {
            Id = id,
            Path = path,
            WatchChanges = false,
            DocumentLayout = new DocumentLayoutOptions { ModelId = "app-settings" },
            ResourceOptions = new FileResourceOptions { CreateBackup = false },
        };

    private static YamlFileSourceOptions YamlOptions(string id, string path) =>
        new()
        {
            Id = id,
            Path = path,
            WatchChanges = false,
            DocumentLayout = new DocumentLayoutOptions { ModelId = "app-settings" },
            ResourceOptions = new FileResourceOptions { CreateBackup = false },
        };

    private static XmlFileSourceOptions XmlOptions(string id, string path) =>
        new()
        {
            Id = id,
            Path = path,
            WatchChanges = false,
            ResourceOptions = new FileResourceOptions { CreateBackup = false },
        };

    private static async Task SeedJsonAsync(string path, int retryCount, string label)
    {
        await using var seed = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.FromJsonFile(JsonOptions("seed", path)))
            );
        });
        var state = seed.GetState<AppSettings>();
        await state.SaveAsync(settings => settings.RetryCount = retryCount);
        await state.SaveAsync(settings => settings.Label = label);
    }

    private static async Task SeedYamlAsync(string path, int retryCount, string label)
    {
        await using var seed = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.FromYamlFile(YamlOptions("seed", path)))
            );
        });
        var state = seed.GetState<AppSettings>();
        await state.SaveAsync(settings => settings.RetryCount = retryCount);
        await state.SaveAsync(settings => settings.Label = label);
    }

    private static async Task SeedXmlAsync(string path, int retryCount, string label)
    {
        await using var seed = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.FromXmlFile(XmlOptions("seed", path)))
            );
        });
        var state = seed.GetState<AppSettings>();
        await state.SaveAsync(settings => settings.RetryCount = retryCount);
        await state.SaveAsync(settings => settings.Label = label);
    }

    private static async Task SeedHistoricalV1YamlAsync(string path, int retryCount)
    {
        await using var seed = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<HistoricalSettingsV1>(model =>
                model.Sources(sources =>
                    sources.FromYamlFile(
                        new YamlFileSourceOptions
                        {
                            Id = "seed",
                            Path = path,
                            WatchChanges = false,
                            DocumentLayout = new DocumentLayoutOptions
                            {
                                ModelId = "historical-settings",
                            },
                            ResourceOptions = new FileResourceOptions { CreateBackup = false },
                        }
                    )
                )
            );
        });
        var state = seed.GetState<HistoricalSettingsV1>();
        await state.SaveAsync(settings => settings.RetryCount = retryCount);
        await state.SaveAsync(settings => settings.NullableLabel = null);
    }

    private static async Task<AppSettings.Fragment> ReadJsonFileAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path);
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var sequence = new System.Buffers.ReadOnlySequence<byte>(
            System.Text.Encoding.UTF8.GetBytes(text)
        );
        return codec.Deserialize(in sequence, default)!;
    }

    private sealed class HistoricalV1ToV3Migration
        : IStateSchemaMigration<HistoricalSettings.Fragment>
    {
        public StateSchemaMetadata SourceSchema => new("historical-settings", 1);

        public StateSchemaMetadata TargetSchema => HistoricalSettings.ConfiglueSchema.ToMetadata();

        public ValueTask<HistoricalSettings.Fragment> MigrateAsync(
            HistoricalSettings.Fragment value,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var builder = value.ToBuilder();
            builder.NewName = Optional<string?>.Present("migrated-from-v1");
            return ValueTaskCompat.FromResult(builder.Build());
        }
    }

    private sealed class FlippingStateReader(
        AppSettings.Fragment first,
        AppSettings.Fragment second
    ) : ISourceReader<AppSettings.Fragment>
    {
        private int _reads;

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            var reads = Interlocked.Increment(ref _reads);
            var fragment = reads == 1 ? first : second;
            return ValueTaskCompat.FromResult(
                StateReadResult<AppSettings.Fragment>.Success(
                    fragment,
                    reads.ToString(System.Globalization.CultureInfo.InvariantCulture)
                )
            );
        }
    }

    private sealed class InMemoryMigrationJournal : IStateStorageMigrationJournal
    {
        private readonly Dictionary<string, StateStorageMigrationProgress> _stored = new();

        public ValueTask<StateStorageMigrationProgress?> ReadAsync(
            string migrationId,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stored.TryGetValue(migrationId, out var progress);
            return ValueTaskCompat.FromResult<StateStorageMigrationProgress?>(progress);
        }

        public ValueTask WriteAsync(
            StateStorageMigrationProgress progress,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(progress);
            cancellationToken.ThrowIfCancellationRequested();
            _stored[progress.MigrationId] = progress;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FlakyMigrationJournal(int failOnWriteCount) : IStateStorageMigrationJournal
    {
        private int _writes;

        public ValueTask<StateStorageMigrationProgress?> ReadAsync(
            string migrationId,
            CancellationToken cancellationToken = default
        )
        {
            _ = migrationId;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult<StateStorageMigrationProgress?>(null);
        }

        public ValueTask WriteAsync(
            StateStorageMigrationProgress progress,
            CancellationToken cancellationToken = default
        )
        {
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _writes) <= failOnWriteCount)
            {
                return ValueTaskCompat.FromException(new IOException("journal unavailable"));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            FullPath = Path.Combine(
                Path.GetTempPath(),
                "Configlue.Tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(FullPath);
        }

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
