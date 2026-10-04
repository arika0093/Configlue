using System.CommandLine;
using System.Linq.Expressions;
using Configlue.CompilerServices;
using Configlue.Provider.Json;
using Configlue.Source.CommandLine;
using Configlue.Testing;

namespace Configlue.Tests;

/// <summary>
/// The single shared contract for typed member-selector parsing (#257).
/// Covers single, deep-nested, nullable/reference conversions, field, method call,
/// captured value, empty/root, unknown/ambiguous schema members, and recursive paths,
/// then requires every migrated caller to produce the same result.
/// </summary>
public sealed class MemberSelectorContractTests
{
    [Test]
    public void SingleMemberParsesToOneSegment()
    {
        Expression<Func<SelectorModel, string?>> selector = static model => model.Label;

        var names = ConfiglueMemberSelector.GetMemberNames(selector, "test selector", "selector");

        names.ShouldBe(["Label"]);
        ConfiglueMemberSelector
            .GetPropertyPath(selector, "test selector", "selector")
            .ShouldBe("Label");
    }

    [Test]
    public void DeepNestedMemberParsesToDottedPath()
    {
        Expression<Func<SelectorModel, string?>> selector = static model =>
            model.Child!.Next!.Label;

        var names = ConfiglueMemberSelector.GetMemberNames(selector, "test selector", "selector");

        names.ShouldBe(["Child", "Next", "Label"]);
        ConfiglueMemberSelector
            .GetPropertyPath(selector, "test selector", "selector")
            .ShouldBe("Child.Next.Label");
    }

    [Test]
    public void NullableAndReferenceConversionsUnwrap()
    {
        Expression<Func<SelectorModel, int?>> nullableSelector = static model => model.RetryCount;
        Expression<Func<SelectorModel, object?>> referenceSelector = static model => model.Label;

        ConfiglueMemberSelector
            .GetMemberNames(nullableSelector, "test selector", "selector")
            .ShouldBe(["RetryCount"]);
        ConfiglueMemberSelector
            .GetMemberNames(referenceSelector, "test selector", "selector")
            .ShouldBe(["Label"]);
    }

    [Test]
    public void FieldMemberIsRejected()
    {
        Expression<Func<SelectorModel, int>> selector = static model => model.Legacy;

        var exception = Should.Throw<ArgumentException>(() =>
            ConfiglueMemberSelector.GetMemberNames(selector, "test selector", "selector")
        );
        exception.ParamName.ShouldBe("selector");
        exception.Message.ShouldContain("generated model properties");
    }

    [Test]
    public void MethodCallIsRejected()
    {
        Expression<Func<SelectorModel, string>> selector = static model => model.Label!.ToString();

        var exception = Should.Throw<ArgumentException>(() =>
            ConfiglueMemberSelector.GetMemberNames(selector, "test selector", "selector")
        );
        exception.ParamName.ShouldBe("selector");
        exception.Message.ShouldContain("property path from its model parameter");
    }

    [Test]
    public void CapturedValueIsRejected()
    {
        var captured = "captured";
        Expression<Func<SelectorModel, string>> selector = model => captured;

        var exception = Should.Throw<ArgumentException>(() =>
            ConfiglueMemberSelector.GetMemberNames(selector, "test selector", "selector")
        );
        exception.ParamName.ShouldBe("selector");
    }

    [Test]
    public void EmptyRootSelectorIsRejected()
    {
        Expression<Func<SelectorModel, SelectorModel>> selector = static model => model;

        var exception = Should.Throw<ArgumentException>(() =>
            ConfiglueMemberSelector.GetMemberNames(selector, "test selector", "selector")
        );
        exception.ParamName.ShouldBe("selector");
        exception.Message.ShouldContain("property path from its model parameter");
    }

    [Test]
    public void UnknownSchemaMemberIsRejected()
    {
        Expression<Func<SelectorModel, string?>> selector = static model => model.Child!.Label;
        var schema = new ConfiglueModelSchema(
            typeof(SelectorModel),
            "selector-model",
            1,
            [new ConfiglueMemberSchema(0, "Label", typeof(string), MergeMode.Replace)]
        );

        var exception = Should.Throw<ArgumentException>(() =>
            ConfiglueMemberSelector.Bind(selector, schema, "test selector", "selector")
        );
        exception.Message.ShouldContain("Child");
    }

    [Test]
    public void AmbiguousSchemaMemberIsRejected()
    {
        var schema = new ConfiglueModelSchema(
            typeof(SelectorModel),
            "selector-model",
            1,
            [
                new ConfiglueMemberSchema(0, "Label", typeof(string), MergeMode.Replace),
                new ConfiglueMemberSchema(1, "Label", typeof(string), MergeMode.Replace),
            ]
        );

        var exception = Should.Throw<ArgumentException>(() =>
            ConfiglueMemberPath.FromNames(schema, "Label")
        );
        exception.Message.ShouldContain("Label");
    }

    [Test]
    public void RecursiveModelPathsBind()
    {
        ConfiglueModelSchema? nodeSchema = null;
        nodeSchema = new ConfiglueModelSchema(
            typeof(SelectorNode),
            "selector-node",
            1,
            [
                new ConfiglueMemberSchema(0, "Name", typeof(string), MergeMode.Replace),
                new ConfiglueMemberSchema(
                    1,
                    "Child",
                    typeof(SelectorNode),
                    MergeMode.Deep,
                    NestedSchemaFactory: () => nodeSchema!
                ),
            ]
        );
        Expression<Func<SelectorNode, string?>> selector = static model => model.Child!.Child!.Name;

        var bound = ConfiglueMemberSelector.Bind(selector, nodeSchema, "test selector", "selector");

        bound.Length.ShouldBe(3);
        bound.ToString().ShouldBe("Child.Child.Name");
    }

    [Test]
    public void WritePlanRouteMatchesStringRoute()
    {
        var typed = StateWritePlan
            .For<AppSettings>()
            .Route(
                static settings => settings.Database!.Port,
                SourceKey<AppSettings>.Named("typed")
            )
            .Route(static settings => settings.Label, SourceKey<AppSettings>.Named("typed"))
            .Build();
        var fromStrings = new StateWritePlan(
            null,
            new Dictionary<string, SourceId>(StringComparer.Ordinal)
            {
                ["Database.Port"] = SourceId.From("typed"),
                ["Label"] = SourceId.From("typed"),
            }
        );

        typed.PropertyRoutes.ShouldBe(fromStrings.PropertyRoutes);
        typed
            .ResolveSourceId("Database.Port")
            .ShouldBe(fromStrings.ResolveSourceId("Database.Port"));
        typed.ResolveSourceId("Label").ShouldBe(fromStrings.ResolveSourceId("Label"));
    }

    [Test]
    public void WritePlanRouteRejectsInvalidSelectors()
    {
        Should.Throw<ArgumentException>(() =>
            StateWritePlan
                .For<AppSettings>()
                .Route(
                    static settings => settings.ToString(),
                    SourceKey<AppSettings>.Named("typed")
                )
        );
    }

    [Test]
    public void JsonMountStoresSamePathAsHelper()
    {
        var single = MountPath(settings => settings.Database);
        var nested = MountPath(settings => settings.Database!.Host);

        single.ShouldBe("Database");
        nested.ShouldBe("Database.Host");
        nested.ShouldBe(
            ConfiglueMemberSelector.GetPropertyPath(
                (Expression<Func<AppSettings, string>>)(static settings => settings.Database!.Host),
                "mounted subtree selector",
                "subtreeSelector"
            )
        );
    }

    [Test]
    public void JsonMountRejectsInvalidSelectors()
    {
        Should.Throw<ArgumentException>(() => MountPath(static settings => settings.ToString()));
    }

    [Test]
    public async Task SourceSetMountMatchesStringMount()
    {
        var typedHost = await ReadMountedHostAsync(useTypedSelector: true);
        var stringHost = await ReadMountedHostAsync(useTypedSelector: false);

        typedHost.ShouldBe("remote.db");
        stringHost.ShouldBe(typedHost);
    }

    [Test]
    public async Task CommandLineTypedMappingMatchesStringMapping()
    {
        var retryOption = new Option<int>("--retry");
        var root = new RootCommand();
        root.Options.Add(retryOption);
        var parseResult = root.Parse(["--retry", "8"]);

        var typed = await ReadRetryCountAsync(
            parseResult,
            mappings => mappings.Map(retryOption, (AppSettings model) => model.RetryCount)
        );
        var fromString = await ReadRetryCountAsync(
            parseResult,
            mappings => mappings.Map(retryOption, "RetryCount")
        );

        typed.ShouldBe(8);
        fromString.ShouldBe(typed);
    }

    [Test]
    public void CommandLineMappingRejectsInvalidSelectors()
    {
        var labelOption = new Option<string?>("--label");
        var builder = new CommandLineMappingBuilder();

        Should.Throw<ArgumentException>(() =>
            builder.Map(labelOption, (AppSettings model) => model.ToString())
        );
    }

    private static string MountPath<TSubtreeModel>(
        Expression<Func<AppSettings, TSubtreeModel?>> selector
    )
    {
        var options = new JsonFileSourceOptions { Path = "contract.json" };
        var sources = new ConfiglueSourceSetBuilder<AppSettings>();
        var registration = new JsonFileRegistration<AppSettings>(
            options,
            sources,
            new ConfiglueSourceRegistration(() => { })
        );
        registration.Mount(selector);
        return options.MountPath!;
    }

    private static async Task<string?> ReadMountedHostAsync(bool useTypedSelector)
    {
        var baseStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("default.db"),
                        Port = Optional<int>.Present(5432),
                    }
                ),
            }
        );
        var remoteStore = new InMemoryStateSource<DatabaseSettings.Fragment>(
            new DatabaseSettings.Fragment { Host = Optional<string>.Present("remote.db") }
        );
        var remote = new StateSource<DatabaseSettings.Fragment>(
            "remote-database",
            remoteStore,
            new StateSourceOptions<DatabaseSettings.Fragment> { Priority = 100 }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "defaults",
                            baseStore,
                            new StateSourceOptions<AppSettings.Fragment>()
                        )
                    );
                    if (useTypedSelector)
                    {
                        sources.AddMounted<
                            AppSettings,
                            AppSettings.Fragment,
                            DatabaseSettings,
                            DatabaseSettings.Fragment
                        >(remote, static model => model.Database);
                    }
                    else
                    {
                        sources.AddMounted<AppSettings.Fragment, DatabaseSettings.Fragment>(
                            remote,
                            "Database"
                        );
                    }
                })
            );
        });

        var value = await context.GetState<AppSettings>().GetValueAsync();
        return value.Database!.Host;
    }

    private static async Task<int> ReadRetryCountAsync(
        ParseResult parseResult,
        Action<CommandLineMappingBuilder> configureMappings
    )
    {
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    var baseStore = new InMemoryStateSource<AppSettings.Fragment>(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "base",
                            baseStore,
                            new StateSourceOptions<AppSettings.Fragment>()
                        )
                    );
                    sources.FromCommandLine(
                        new CommandLineSourceOptions
                        {
                            Id = "command-line",
                            ParseResult = parseResult,
                            Priority = 100,
                        },
                        configureMappings
                    );
                })
            );
        });

        var value = await context.GetState<AppSettings>().GetValueAsync();
        return value.RetryCount;
    }

    private sealed class SelectorModel
    {
        public string? Label { get; set; }

        public int RetryCount { get; set; }

        public int Legacy = 0;

        public SelectorChild? Child { get; set; }
    }

    private sealed class SelectorChild
    {
        public string? Label { get; set; }

        public SelectorChild? Next { get; set; }
    }

    private sealed class SelectorNode
    {
        public string? Name { get; set; }

        public SelectorNode? Child { get; set; }
    }
}
