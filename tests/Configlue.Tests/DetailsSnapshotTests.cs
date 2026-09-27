using Configlue;
using Configlue.Source.Environment;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class DetailsSnapshotTests
{
    [Test]
    public async Task GetDetailsAsync_ExposesTypedLeavesWithProvenance()
    {
        await using var context = Configlue.CreateContext(builder =>
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
        var options = context.GetOptions<AppSettings>();
        var details = await options.GetDetailsAsync();

        (details.RetryCount.Value).ShouldBe(8);
        ((int)details.RetryCount).ShouldBe(8);
        (details.RetryCount.IsEditable).ShouldBeTrue();
        (details.RetryCount.Editability).ShouldBe(ConfiglueEditability.Editable);
        (details.RetryCount.Source?.Key).ShouldBe("override");
        (details.RetryCount.Sources.Count).ShouldBe(2);
        (details.RetryCount.Sources[0].Source.Key).ShouldBe("override");
        (details.RetryCount.Sources[0].IsPresent).ShouldBeTrue();
        (details.RetryCount.Sources[0].Value).ShouldBe(8);
        (details.RetryCount.Sources[1].Source.Key).ShouldBe("base");
        (details.RetryCount.Sources[1].Value).ShouldBe(4);
        (details.Label.Value).ShouldBe("base");
        (details.Label.Source?.Key).ShouldBe("base");

        string? name = details.Label;
        (name).ShouldBe("base");
    }

    [Test]
    public async Task GetDetailsAsync_ReportsShadowingThroughEditability()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__RETRYCOUNT"] = "9",
        };
        await using var context = Configlue.CreateContext(builder =>
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
        var options = context.GetOptions<AppSettings>();
        var details = await options.GetDetailsAsync();

        (details.RetryCount.Value).ShouldBe(9);
        (details.RetryCount.IsEditable).ShouldBeFalse();
        (details.RetryCount.Editability).ShouldBe(ConfiglueEditability.Shadowed);
        (details.RetryCount.Source?.Kind).ShouldBe("Environment");
    }

    [Test]
    public async Task GetDetailsAsync_NavigatesNestedMembersAndCollections()
    {
        await using var context = Configlue.CreateContext(builder =>
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
        var options = context.GetOptions<AppSettings>();
        var details = await options.GetDetailsAsync();

        (details.Database!.Host.Value).ShouldBe("db.local");
        (details.Database.Host.Source?.Key).ShouldBe("base");
        (details.Plugins!.Value.Count).ShouldBe(2);
        (details.Plugins.Elements.Count).ShouldBe(2);
        (details.Plugins.Elements[0].Value).ShouldBe("admin");
        (details.Plugins.Elements[0].Contributions.Count).ShouldBe(1);
        (details.Plugins.Elements[0].Contributions[0].Source.Key).ShouldBe("base");
    }

    [Test]
    public async Task GetDetailsAsync_SupportsArraysSetsAndNestedCollections()
    {
        await using var context = Configlue.CreateContext(builder =>
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
                                    Children = Optional<List<OwnershipChild>>.Present(
                                        [new OwnershipChild { Name = "first" }]
                                    ),
                                }
                            )
                        )
                    )
                )
            );
        });
        var options = context.GetOptions<OwnershipSettings>();
        var details = await options.GetDetailsAsync();

        (details.ArrayValues!.Value).ShouldBe(["a", "b"]);
        (details.ArrayValues.Source?.Key).ShouldBe("base");
        (details.SetValues!.Value).ShouldBe(["x"]);
        (details.Children!.Value.Count).ShouldBe(1);
        (details.Children.Elements.Count).ShouldBe(1);
        (details.Children.Elements[0].Value!.Name).ShouldBe("first");
    }
}
