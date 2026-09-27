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
            var value = await context.GetOptions<AppSettings>().GetValueAsync();
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
            var value = await context.GetOptions<AppSettings>().GetValueAsync();
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
                await context.GetOptions<AppSettings>().GetValueAsync()
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
                await context.GetOptions<AppSettings>().GetValueAsync()
            );
            (exception.Message).ShouldContain("unmatched tokens");
        }

        var validParseResult = root.Parse(["--retry", "5"]);
        await using var writableContext = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.WriteRoute = StateWriteRoute.To("command-line");
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
            await writableContext.GetOptions<AppSettings>().SaveAsync(value => value.RetryCount = 7)
        );
    }

    [Test]
    public async Task IncompatibleParsedValueTypeFailsWithJsonConversionDetails()
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
                await context.GetOptions<AppSettings>().GetValueAsync()
            );
            (exception.Message).ShouldContain("RetryCount");
        }
    }

    [Test]
    public void DuplicateCommandLineModelMappingsAreRejected()
    {
        var firstOption = new Option<int>("--first");
        var secondOption = new Option<int>("--second");
        var root = new RootCommand();
        root.Options.Add(firstOption);
        root.Options.Add(secondOption);
        var parseResult = root.Parse(["--first", "1", "--second", "2"]);

        Should.Throw<ArgumentException>(() =>
            Configlue.CreateContext(builder =>
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
                                mappings.Map(firstOption, "RetryCount");
                                mappings.Map(secondOption, "RetryCount");
                            }
                        )
                    )
                )
            )
        );
    }

    private static ConfiglueContext CreateContext(
        ParseResult parseResult,
        Action<CommandLineMappingBuilder> configureMappings
    ) =>
        Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    var baseStore = new InMemoryStateStore<AppSettings.Fragment>(
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
