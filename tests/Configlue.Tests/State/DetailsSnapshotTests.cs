using Configlue;
using Configlue.Source.Environment;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class DetailsSnapshotTests
{
    [Test]
    public async Task GetDetailsAsync_ExposesTypedLeavesWithProvenance()
    {
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "base",
                            new InMemoryStateStore<AppSettings.Fragment>(
                                new AppSettings.Fragment
                                {
                                    RetryCount = Optional<int>.Present(4),
                                    Label = Optional<string?>.Present("base"),
                                }
                            )
                        )
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "override",
                            new InMemoryStateStore<AppSettings.Fragment>(
                                new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
                            ),
                            writer: new InMemoryStateStore<AppSettings.Fragment>(),
                            priority: 100
                        )
                    );
                })
            );
        });
        var options = context.GetState<AppSettings>();
        var details = await options.GetDetailsAsync();

        var overrideKey = details.RetryCount.Source!.Key;
        var baseKey = details.Label.Source!.Key;
        (overrideKey == "override").ShouldBeFalse();
        (baseKey == "base").ShouldBeFalse();
        (details.RetryCount.Value).ShouldBe(8);
        ((int)details.RetryCount).ShouldBe(8);
        (details.RetryCount.IsEditable).ShouldBeTrue();
        (details.RetryCount.Editability).ShouldBe(ConfiglueEditability.Editable);
        (details.RetryCount.Source?.Key).ShouldBe(overrideKey);
        (details.RetryCount.Sources.Count).ShouldBe(3);
        (details.RetryCount.Sources[0].Source.Key).ShouldBe(overrideKey);
        (details.RetryCount.Sources[0].IsPresent).ShouldBeTrue();
        (details.RetryCount.Sources[0].Value).ShouldBe(8);
        (details.RetryCount.Sources[0].IsShadowed).ShouldBeFalse();
        (details.RetryCount.Sources[1].Source.Key).ShouldBe(baseKey);
        (details.RetryCount.Sources[1].Value).ShouldBe(4);
        (details.RetryCount.Sources[1].IsShadowed).ShouldBeTrue();
        (details.Label.Value).ShouldBe("base");
        (details.Label.Source?.Key).ShouldBe(baseKey);
        (details.Label.Sources[0].State).ShouldBe(ConfigSourceValueState.Missing);
        (details.RetryCount.Sources[2].Source.Kind).ShouldBe("model-defaults");

        string? name = details.Label;
        (name).ShouldBe("base");

        var nextDetails = await options.GetDetailsAsync();
        (nextDetails.RetryCount.Source?.Key).ShouldBe(overrideKey);
    }

    [Test]
    public async Task GetDetailsAsync_ReportsModelDefaultsAsTheEditableBaseline()
    {
        var missing = new InMemoryStateStore<AppSettings.Fragment>();
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("local", missing, writer: missing)])
        );

        var details = await options.GetDetailsAsync();

        (details.RetryCount.Value).ShouldBe(3);
        (details.RetryCount.Source?.Kind).ShouldBe("model-defaults");
        (details.RetryCount.Source?.DisplayName).ShouldBe("Model defaults");
        (details.RetryCount.Source?.CanWrite == false).ShouldBeTrue();
        (details.RetryCount.Source?.CanWatch == false).ShouldBeTrue();
        (details.RetryCount.IsEditable).ShouldBeTrue();
        (details.RetryCount.Sources[^1].Value).ShouldBe(3);
        (details.Enabled.Value).ShouldBeTrue();
        (details.Label.Source?.Kind).ShouldBe("model-defaults");
        (details.Label.Value).ShouldBe("default");
    }

    [Test]
    public async Task GetDetailsAsync_IncludesImplicitClrDefaults()
    {
        await using var options = new ConfiglueRuntime<
            ClrDefaultSettings,
            ClrDefaultSettings.Fragment
        >(
            new StateSourceSet<ClrDefaultSettings.Fragment>([
                new("empty", new InMemoryStateStore<ClrDefaultSettings.Fragment>()),
            ])
        );

        var details = await options.GetDetailsAsync();

        (details.RetryCount.Value).ShouldBe(0);
        (details.Enabled.Value).ShouldBeFalse();
        (details.Name.Value).ShouldBeNull();
        (details.RetryCount.Source?.Kind).ShouldBe("model-defaults");
        (details.RetryCount.Sources.Count).ShouldBe(2);
    }

    [Test]
    public async Task GetDetailsAsync_ReportsUnavailableSourcesInFallbackSnapshots()
    {
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new(
                    "remote-internal-id",
                    new FixedReader<AppSettings.Fragment>(
                        StateReadResult<AppSettings.Fragment>.Unavailable("remote")
                    ),
                    priority: 100,
                    fallbackCondition: StateFallbackCondition.Unavailable,
                    physicalOrigin: "https://example.invalid/settings"
                ),
                new(
                    "base-internal-id",
                    new InMemoryStateStore<AppSettings.Fragment>(
                        new AppSettings.Fragment { Label = Optional<string?>.Present("local") }
                    ),
                    priority: 0
                ),
            ])
        );
        await using (options)
        {
            var details = await options.GetDetailsAsync();

            (details.Label.Value).ShouldBe("local");
            (details.Label.Sources[0].State).ShouldBe(ConfigSourceValueState.Unavailable);
            (details.Label.Sources[1].State).ShouldBe(ConfigSourceValueState.Present);
            (details.Label.Sources[0].Source.Key == "remote-internal-id").ShouldBeFalse();
            (details.Label.Sources[1].Source.Key == "base-internal-id").ShouldBeFalse();
        }
    }

    [Test]
    public async Task GetDetailsAsync_UsesOneReadForTheEntireGeneratedTree()
    {
        var reader = new CountingReader<AppSettings.Fragment>(
            StateReadResult<AppSettings.Fragment>.Success(
                new AppSettings.Fragment
                {
                    Label = Optional<string?>.Present("snapshot"),
                    Database = Optional<DatabaseSettings.Fragment?>.Present(
                        new DatabaseSettings.Fragment
                        {
                            Host = Optional<string>.Present("db.local"),
                        }
                    ),
                    Plugins = Optional<IReadOnlyList<string>>.Present(["one", "two"]),
                },
                "revision-1"
            )
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("counted", reader)])
        );

        var details = await options.GetDetailsAsync();

        (details.Label.Value).ShouldBe("snapshot");
        (details.Database!.Host.Value).ShouldBe("db.local");
        (details.Plugins!.Elements.Count).ShouldBe(2);
        (reader.ReadCount).ShouldBe(1);
    }

    [Test]
    public async Task GetDetailsAsync_ReportsShadowingThroughEditability()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__RETRYCOUNT"] = "9",
        };
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.WriteRoute = StateWriteRoute.To("file");
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "file",
                            new InMemoryStateStore<AppSettings.Fragment>(),
                            writer: new InMemoryStateStore<AppSettings.Fragment>()
                        )
                    );
                    sources.Add(
                        EnvironmentStateSource.FromEnvironment<AppSettings, AppSettings.Fragment>(
                            "environment",
                            "APP",
                            priority: 400,
                            environmentVariables: () => variables
                        )
                    );
                });
            });
        });
        var options = context.GetState<AppSettings>();
        var details = await options.GetDetailsAsync();

        (details.RetryCount.Value).ShouldBe(9);
        (details.RetryCount.IsEditable).ShouldBeFalse();
        (details.RetryCount.Editability).ShouldBe(ConfiglueEditability.Shadowed);
        (details.RetryCount.Source?.Kind).ShouldBe("Environment");
    }

    [Test]
    public async Task GetDetailsAsync_UsesResolverOrderForEqualPriorityShadowing()
    {
        var policy = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("policy") }
        );
        var user = new InMemoryStateStore<AppSettings.Fragment>();
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("policy", policy, priority: 100),
                new("user", user, priority: 100, writer: user),
            ]),
            StateWriteRoute.To("user")
        );

        var details = await options.GetDetailsAsync();

        (details.Label.Editability).ShouldBe(ConfiglueEditability.Shadowed);
        (details.Label.Sources[0].IsShadowed).ShouldBeFalse();
        (details.Label.Sources[1].IsPresent).ShouldBeFalse();
    }

    [Test]
    public async Task GetDetailsAsync_DistinguishesReadOnlyAndMissingWriteTargets()
    {
        var readOnly = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("read-only") }
        );
        await using (
            var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                new StateSourceSet<AppSettings.Fragment>([new("policy", readOnly)]),
                StateWriteRoute.To("policy")
            )
        )
        {
            var details = await options.GetDetailsAsync();
            (details.Label.Editability).ShouldBe(ConfiglueEditability.ReadOnly);
        }

        await using (
            var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                new StateSourceSet<AppSettings.Fragment>([new("policy", readOnly)])
            )
        )
        {
            var details = await options.GetDetailsAsync();
            (details.Label.Editability).ShouldBe(ConfiglueEditability.NoWriteTarget);
        }
    }

    [Test]
    public async Task GetDetailsAsync_NavigatesNestedMembersAndCollections()
    {
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "base",
                            new InMemoryStateStore<AppSettings.Fragment>(
                                new AppSettings.Fragment
                                {
                                    Database = Optional<DatabaseSettings.Fragment?>.Present(
                                        new DatabaseSettings.Fragment
                                        {
                                            Host = Optional<string>.Present("db.local"),
                                        }
                                    ),
                                    Plugins = Optional<IReadOnlyList<string>>.Present([
                                        "admin",
                                        "metrics",
                                    ]),
                                }
                            )
                        )
                    )
                )
            );
        });
        var options = context.GetState<AppSettings>();
        var details = await options.GetDetailsAsync();

        (details.Database!.Host.Value).ShouldBe("db.local");
        (details.Database.Host.Source?.Key).ShouldBe(details.Database.Host.Sources[0].Source.Key);
        (details.Plugins!.Value.Count).ShouldBe(2);
        (details.Plugins.Elements.Count).ShouldBe(2);
        (details.Plugins.Elements[0].Value).ShouldBe("admin");
        (details.Plugins.Elements[0].Contributions.Count).ShouldBe(1);
        (details.Plugins.Elements[0].Contributions[0].Source.Key).ShouldBe(
            details.Plugins.Sources[0].Source.Key
        );
    }

    [Test]
    public async Task GetDetailsAsync_SupportsArraysSetsAndNestedCollections()
    {
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<OwnershipSettings>(model =>
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<OwnershipSettings.Fragment>(
                            "base",
                            new InMemoryStateStore<OwnershipSettings.Fragment>(
                                new OwnershipSettings.Fragment
                                {
                                    ArrayValues = Optional<string[]>.Present(["a", "b"]),
                                    SetValues = Optional<ISet<string>>.Present(
                                        new HashSet<string>(["x"])
                                    ),
                                    Children = Optional<List<OwnershipChild>>.Present([
                                        new OwnershipChild { Name = "first" },
                                    ]),
                                }
                            )
                        )
                    )
                )
            );
        });
        var options = context.GetState<OwnershipSettings>();
        var details = await options.GetDetailsAsync();

        (details.ArrayValues!.Value).ShouldBe(["a", "b"]);
        (details.ArrayValues.Source?.Key).ShouldBe(details.ArrayValues.Sources[0].Source.Key);
        (details.SetValues!.Value).ShouldBe(["x"]);
        (details.Children!.Value.Count).ShouldBe(1);
        (details.Children.Elements.Count).ShouldBe(1);
        (details.Children.Elements[0].Value!.Name).ShouldBe("first");
    }

    [Test]
    public async Task GetDetailsAsync_ReportsCollectionOwnershipAndShadowing()
    {
        var appendOptions = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new(
                    "high-internal-id",
                    new InMemoryStateStore<AppSettings.Fragment>(
                        new AppSettings.Fragment
                        {
                            Plugins = Optional<IReadOnlyList<string>>.Present(["high"]),
                        }
                    ),
                    priority: 100
                ),
                new(
                    "low-internal-id",
                    new InMemoryStateStore<AppSettings.Fragment>(
                        new AppSettings.Fragment
                        {
                            Plugins = Optional<IReadOnlyList<string>>.Present(["low"]),
                        }
                    ),
                    priority: 0
                ),
            ])
        );
        await using (appendOptions)
        {
            var details = await appendOptions.GetDetailsAsync();

            (details.Plugins!.Value).ShouldBe(["low", "high"]);
            (details.Plugins.Source).ShouldBeNull();
            (details.Plugins.Sources[0].IsShadowed).ShouldBeFalse();
            (details.Plugins.Sources[1].IsShadowed).ShouldBeFalse();
        }

        var replaceOptions = new ConfiglueRuntime<
            ReplaceCollectionSettings,
            ReplaceCollectionSettings.Fragment
        >(
            new StateSourceSet<ReplaceCollectionSettings.Fragment>([
                new(
                    "preferred",
                    new InMemoryStateStore<ReplaceCollectionSettings.Fragment>(
                        new ReplaceCollectionSettings.Fragment
                        {
                            Values = Optional<IReadOnlyList<string>>.Present(["preferred"]),
                        }
                    ),
                    priority: 100
                ),
                new(
                    "fallback",
                    new InMemoryStateStore<ReplaceCollectionSettings.Fragment>(
                        new ReplaceCollectionSettings.Fragment
                        {
                            Values = Optional<IReadOnlyList<string>>.Present(["fallback"]),
                        }
                    ),
                    priority: 0
                ),
            ])
        );
        await using (replaceOptions)
        {
            var details = await replaceOptions.GetDetailsAsync();

            (details.Values!.Value).ShouldBe(["preferred"]);
            (details.Values.Sources[0].IsShadowed).ShouldBeFalse();
            (details.Values.Sources[1].IsShadowed).ShouldBeTrue();
        }
    }

    private sealed class FixedReader<T>(StateReadResult<T> result) : ISourceReader<T>
    {
        public ValueTask<StateReadResult<T>> ReadAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CountingReader<T>(StateReadResult<T> result) : ISourceReader<T>
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public ValueTask<StateReadResult<T>> ReadAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);
            return ValueTask.FromResult(result);
        }
    }
}
