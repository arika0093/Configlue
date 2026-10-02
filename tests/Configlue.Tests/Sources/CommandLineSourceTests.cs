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

    [Test]
    public async Task LaterMappingsWinWhenSeveralSymbolsTargetOneMember()
    {
        var firstOption = new Option<int>("--first");
        var secondOption = new Option<int>("--second");
        var root = new RootCommand();
        root.Options.Add(firstOption);
        root.Options.Add(secondOption);
        var parseResult = root.Parse(["--first", "1", "--second", "2"]);

        await using (
            var context = CreateContext(
                parseResult,
                mappings =>
                {
                    mappings.Map(firstOption, "RetryCount");
                    mappings.Map(secondOption, "RetryCount");
                }
            )
        )
        {
            (await context.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(2);
        }

        await using (
            var reversed = CreateContext(
                parseResult,
                mappings =>
                {
                    mappings.Map(secondOption, "RetryCount");
                    mappings.Map(firstOption, "RetryCount");
                }
            )
        )
        {
            (await reversed.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(1);
        }
    }

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
    public async Task CollectionOptionsBindWithoutJsonSerialization()
    {
        var pluginOption = new Option<string[]>("--plugin");
        var root = new RootCommand();
        root.Options.Add(pluginOption);

        await using (
            var context = CreateContext(
                root.Parse(["--plugin", "nord", "--plugin", "dracula"]),
                mappings => mappings.Map(pluginOption, "Plugins")
            )
        )
        {
            var value = await context.GetState<AppSettings>().GetValueAsync();
            (value.Plugins).ShouldBe(["nord", "dracula"]);
        }
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
                        new StateSource<AppSettings.Fragment>("base", baseStore, writer: baseStore)
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
