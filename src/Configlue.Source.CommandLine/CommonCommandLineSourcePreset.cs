using System.CommandLine;
using Configlue;
using Configlue.Source.Common;
using Configlue.Source.Presets;

namespace Configlue.Source.CommandLine;

/// <summary>Adds command-line sources to the common layered preset.</summary>
public static class CommonCommandLineSourcePreset
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
            .Priority(400);
    }

    /// <summary>Registers the legacy common preset with mapped command-line overrides.</summary>
    public static void UseCommonSources<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        CommonSourceOptions options,
        ParseResult parseResult,
        Action<CommandLineMappingBuilder> configureMappings
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(configureMappings);

        model.UseCommonSources(options);
        model.Sources(sources =>
            sources.FromCommandLine(
                new CommandLineSourceOptions
                {
                    Id = "common.commandLine",
                    ParseResult = parseResult,
                    Priority = 4,
                    FallbackCondition = StateFallbackCondition.NotFound,
                },
                configureMappings
            )
        );
    }
}
