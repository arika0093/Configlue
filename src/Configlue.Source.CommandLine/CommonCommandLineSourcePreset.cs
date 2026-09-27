using System.CommandLine;
using Configlue;
using Configlue.Source.Common;

namespace Configlue.Source.CommandLine;

/// <summary>Adds command-line overrides to the common layered source preset.</summary>
public static class CommonCommandLineSourcePreset
{
    /// <summary>Registers the common sources together with mapped command-line overrides.</summary>
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
                    Priority = 500,
                    FallbackCondition = StateFallbackCondition.NotFound,
                },
                configureMappings
            )
        );
    }
}
