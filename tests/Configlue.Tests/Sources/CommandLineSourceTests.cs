using System.CommandLine;
using Configlue.Source.CommandLine;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class CommandLineSourceTests
{
    [Test]
    public async Task MapsExplicitRootAndSubcommandOptionsAndIgnoresParserDefaults()
    {
        var retryOption = new Option<int>("--retry") { DefaultValueFactory = _ => 12 };
        var labelOption = new Option<string>("--label");
        var runCommand = new Command("run");
        runCommand.Options.Add(labelOption);
        var root = new RootCommand();
        root.Options.Add(retryOption);
        root.Subcommands.Add(runCommand);

        await using (
            var context = CreateContext(
                root.Parse(["run", "--label", "cli"]),
                mappings =>
                {
                    mappings.Map(retryOption, "RetryCount");
                    mappings.Map(labelOption, "Label");
                }
            )
        )
        {
            var value = await context.GetState<AppSettings>().GetValueAsync();
            (value.RetryCount).ShouldBe(4);
            (value.Label).ShouldBe("cli");
        }

        await using (
            var context = CreateContext(
                root.Parse(["--retry", "8", "run", "--label", "explicit"]),
                mappings =>
                {
                    mappings.Map(retryOption, "RetryCount");
                    mappings.Map(labelOption, "Label");
                }
            )
        )
        {
            var value = await context.GetState<AppSettings>().GetValueAsync();
            (value.RetryCount).ShouldBe(8);
            (value.Label).ShouldBe("explicit");
        }
    }

    [Test]
    public async Task InvalidParseResultsFailAndCommandLineSourceIsReadOnly()
    {
        var retryOption = new Option<int>("--retry");
        var root = new RootCommand();
        root.Options.Add(retryOption);
        var invalidParseResult = root.Parse(["--retry", "invalid"]);

        await using (
            var context = CreateContext(
                invalidParseResult,
                mappings => mappings.Map(retryOption, "RetryCount")
            )
        )
        {
            await Should.ThrowAsync<FormatException>(async () =>
                await context.GetState<AppSettings>().GetValueAsync()
            );
        }

        root.TreatUnmatchedTokensAsErrors = false;
        var unmatchedParseResult = root.Parse(["--retry", "5", "passthrough"]);
        await using (
            var context = CreateContext(
                unmatchedParseResult,
                mappings => mappings.Map(retryOption, "RetryCount")
            )
        )
        {
            var exception = await Should.ThrowAsync<FormatException>(async () =>
                await context.GetState<AppSettings>().GetValueAsync()
            );
            (exception.Message).ShouldContain("unmatched tokens");
        }

        var validParseResult = root.Parse(["--retry", "5"]);
        await using var writableContext = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("command-line"));
                model.Sources(sources =>
                    sources.FromCommandLine(
                        new CommandLineSourceOptions
                        {
                            Id = "command-line",
                            ParseResult = validParseResult,
                        },
                        mappings => mappings.Map(retryOption, "RetryCount")
                    )
                );
            });
        });

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await writableContext.GetState<AppSettings>().SaveAsync(value => value.RetryCount = 7)
        );
    }

    [Test]
    public async Task IncompatibleParsedValueTypeFailsWithConversionDetails()
    {
        var retryOption = new Option<string>("--retry");
        var root = new RootCommand();
        root.Options.Add(retryOption);
        var context = CreateContext(
            root.Parse(["--retry", "not-a-number"]),
            mappings => mappings.Map(retryOption, "RetryCount")
        );

        await using (context)
        {
            var exception = await Should.ThrowAsync<FormatException>(async () =>
                await context.GetState<AppSettings>().GetValueAsync()
            );
            (exception.Message).ShouldContain("RetryCount");
        }
    }

    // Duplicate handling (last-wins) and typed-sequence binding are covered
    // once in TextAssignmentBinderTests; command-line tests keep transport
    // concerns only (extraction, converters, parser errors, and propagation).
    [Test]
    public void MappingTheSameSymbolToTheSamePathTwiceIsRejected()
    {
        var retryOption = new Option<int>("--retry");
        var root = new RootCommand();
        root.Options.Add(retryOption);
        var parseResult = root.Parse(["--retry", "1"]);

        Should.Throw<ArgumentException>(() =>
            ConfiglueApp.CreateContext(builder =>
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                        sources.FromCommandLine(
                            new CommandLineSourceOptions
                            {
                                Id = "command-line",
                                ParseResult = parseResult,
                            },
                            mappings =>
                            {
                                mappings.Map(retryOption, "RetryCount");
                                mappings.Map(retryOption, "RetryCount");
                            }
                        )
                    )
                )
            )
        );
    }

    [Test]
    public async Task ConvertedMappingsSplitOneArgumentIntoSeveralMembers()
    {
        var databaseOption = new Option<string>("--database");
        var root = new RootCommand();
        root.Options.Add(databaseOption);

        await using (
            var context = CreateContext(
                root.Parse(["--database", "db.example.test:6432"]),
                mappings =>
                {
                    mappings.Map(
                        databaseOption,
                        "Database.Host",
                        static value => value?.Split(':')[0]
                    );
                    mappings.Map(
                        databaseOption,
                        "Database.Port",
                        static value =>
                            int.Parse(
                                value?.Split(':')[1] ?? "0",
                                System.Globalization.CultureInfo.InvariantCulture
                            )
                    );
                }
            )
        )
        {
            var value = await context.GetState<AppSettings>().GetValueAsync();
            (value.Database!.Host).ShouldBe("db.example.test");
            (value.Database.Port).ShouldBe(6432);
        }
    }

    [Test]
    public async Task NullConverterClearsNullableValueTypes()
    {
        var intOption = new Option<string>("--int");
        var boolOption = new Option<string>("--bool");
        var kindOption = new Option<string>("--kind");
        var root = new RootCommand();
        root.Options.Add(intOption);
        root.Options.Add(boolOption);
        root.Options.Add(kindOption);
        var parseResult = root.Parse(["--int", "clear", "--bool", "clear", "--kind", "clear"]);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<CliNullable195Settings>(model =>
                model.Sources(sources =>
                    sources.FromCommandLine(
                        new CommandLineSourceOptions
                        {
                            Id = "command-line",
                            ParseResult = parseResult,
                        },
                        mappings =>
                        {
                            mappings.Map<string, int?>(
                                intOption,
                                "IntValue",
                                static _ => null
                            );
                            mappings.Map<string, bool?>(
                                boolOption,
                                "BoolValue",
                                static _ => null
                            );
                            mappings.Map<string, CliNullable195Kind?>(
                                kindOption,
                                "Kind",
                                static _ => null
                            );
                        }
                    )
                )
            );
        });

        var value = await context.GetState<CliNullable195Settings>().GetValueAsync();
        (value.IntValue).ShouldBeNull();
        (value.BoolValue).ShouldBeNull();
        (value.Kind).ShouldBeNull();
    }

    [Test]
    public async Task NullConverterClearsReferenceType()
    {
        var labelOption = new Option<string>("--label");
        var root = new RootCommand();
        root.Options.Add(labelOption);
        var parseResult = root.Parse(["--label", "clear"]);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<CliNullable195Settings>(model =>
                model.Sources(sources =>
                    sources.FromCommandLine(
                        new CommandLineSourceOptions
                        {
                            Id = "command-line",
                            ParseResult = parseResult,
                        },
                        mappings =>
                            mappings.Map<string, string?>(
                                labelOption,
                                "Label",
                                static _ => null
                            )
                    )
                )
            );
        });

        var value = await context.GetState<CliNullable195Settings>().GetValueAsync();
        (value.Label).ShouldBeNull();
    }

    [Test]
    public async Task NullableCollectionElementsAndDictionaryValuesAcceptNull()
    {
        var scoresOption = new Option<string>("--scores");
        var lookupOption = new Option<string>("--lookup");
        var root = new RootCommand();
        root.Options.Add(scoresOption);
        root.Options.Add(lookupOption);
        var parseResult = root.Parse(["--scores", "x", "--lookup", "y"]);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<CliNullable195Settings>(model =>
                model.Sources(sources =>
                    sources.FromCommandLine(
                        new CommandLineSourceOptions
                        {
                            Id = "command-line",
                            ParseResult = parseResult,
                        },
                        mappings =>
                        {
                            mappings.Map<string, List<int?>>(
                                scoresOption,
                                "Scores",
                                static _ => new List<int?> { 1, null, 3 }
                            );
                            mappings.Map<string, Dictionary<string, int?>>(
                                lookupOption,
                                "Lookup",
                                static _ => new Dictionary<string, int?>
                                {
                                    ["a"] = 1,
                                    ["b"] = null,
                                }
                            );
                        }
                    )
                )
            );
        });

        var value = await context.GetState<CliNullable195Settings>().GetValueAsync();
        (value.Scores).ShouldBe(new int?[] { 1, null, 3 });
        (value.Lookup!["a"]).ShouldBe(1);
        (value.Lookup!["b"]).ShouldBeNull();
        (value.Lookup.ContainsKey("b")).ShouldBeTrue();
    }

    [Test]
    public async Task NullConverterClearsNullableCollections()
    {
        var scoresOption = new Option<string>("--scores");
        var lookupOption = new Option<string>("--lookup");
        var root = new RootCommand();
        root.Options.Add(scoresOption);
        root.Options.Add(lookupOption);
        var parseResult = root.Parse(["--scores", "x", "--lookup", "y"]);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<CliNullable195Settings>(model =>
                model.Sources(sources =>
                    sources.FromCommandLine(
                        new CommandLineSourceOptions
                        {
                            Id = "command-line",
                            ParseResult = parseResult,
                        },
                        mappings =>
                        {
                            mappings.Map<string, List<int?>>(
                                scoresOption,
                                "Scores",
                                static _ => null
                            );
                            mappings.Map<string, Dictionary<string, int?>>(
                                lookupOption,
                                "Lookup",
                                static _ => null
                            );
                        }
                    )
                )
            );
        });

        var value = await context.GetState<CliNullable195Settings>().GetValueAsync();
        (value.Scores).ShouldBeNull();
        (value.Lookup).ShouldBeNull();
    }

    [Test]
    public async Task NullConverterForNonNullableValueTypeFails()
    {
        var retryOption = new Option<string>("--retry");
        var root = new RootCommand();
        root.Options.Add(retryOption);
        var parseResult = root.Parse(["--retry", "clear"]);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<CliNullable195Settings>(model =>
                model.Sources(sources =>
                    sources.FromCommandLine(
                        new CommandLineSourceOptions
                        {
                            Id = "command-line",
                            ParseResult = parseResult,
                        },
                        mappings =>
                            mappings.Map<string, string?>(
                                retryOption,
                                "RetryCount",
                                static _ => null
                            )
                    )
                )
            );
        });

        var exception = await Should.ThrowAsync<FormatException>(async () =>
            await context.GetState<CliNullable195Settings>().GetValueAsync()
        );
        (exception.Message).ShouldContain("RetryCount");
    }

    [Test]
    public async Task UnspecifiedOptionPreservesDefaults()
    {
        var intOption = new Option<string>("--int");
        var root = new RootCommand();
        root.Options.Add(intOption);
        var parseResult = root.Parse([]);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<CliNullable195Settings>(model =>
                model.Sources(sources =>
                    sources.FromCommandLine(
                        new CommandLineSourceOptions
                        {
                            Id = "command-line",
                            ParseResult = parseResult,
                        },
                        mappings =>
                            mappings.Map<string, int?>(
                                intOption,
                                "IntValue",
                                static _ => null
                            )
                    )
                )
            );
        });

        var value = await context.GetState<CliNullable195Settings>().GetValueAsync();
        (value.IntValue).ShouldBe(42);
        (value.BoolValue).ShouldBe(true);
        (value.Label).ShouldBe("default");
        (value.RetryCount).ShouldBe(4);
    }

    private static ConfiglueContext CreateContext(
        ParseResult parseResult,
        Action<CommandLineMappingBuilder> configureMappings
    ) =>
        ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    var baseStore = new InMemoryStateSource<AppSettings.Fragment>(
                        new AppSettings.Fragment
                        {
                            RetryCount = Optional<int>.Present(4),
                            Label = Optional<string?>.Present("base"),
                        }
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("base", baseStore, new StateSourceOptions<AppSettings.Fragment> { Writer = baseStore })
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
}

[ConfiglueModel("cli-nullable-195", Version = 1)]
public partial class CliNullable195Settings
{
    public int? IntValue { get; set; } = 42;

    public bool? BoolValue { get; set; } = true;

    public CliNullable195Kind? Kind { get; set; } = CliNullable195Kind.Alpha;

    public string? Label { get; set; } = "default";

    public int RetryCount { get; set; } = 4;

    public List<int?>? Scores { get; set; } = new() { 1, 2 };

    public Dictionary<string, int?>? Lookup { get; set; } = new() { ["base"] = 1 };
}

public enum CliNullable195Kind
{
    None,

    Alpha,

    Beta,
}
