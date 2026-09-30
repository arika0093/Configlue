using System.CommandLine;
using Configlue;
using Configlue.Source.Presets;

namespace Configlue.Source.CommandLine;

/// <summary>Adds command-line sources to the common layered preset.</summary>
public static class CommonCommandLineSourceExtensions
{
    /// <summary>Registers a command-line source after the environment layer.</summary>
    public static CommonCustomSourceBuilder WithCommandLine(
        this CommonSourceBuilder sources,
        ParseResult parseResult,
        Action<CommandLineMappingBuilder> configureMappings,
        string id = "common.commandLine"
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(configureMappings);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return sources
            .WithCustom(
                CommonSourceLayer.Environment,
                sourceSet =>
                    sourceSet.FromCommandLine(
                        new CommandLineSourceOptions
                        {
                            Id = id,
                            ParseResult = parseResult,
                            FallbackCondition = StateFallbackCondition.NotFound,
                        },
                        configureMappings
                    )
            )
            .Priority(401);
    }
}
