using Configlue;
using Configlue.Examples.Shared;
using Configlue.Source.Presets;
using Configlue.State;
using Shouldly;
using TUnit.Core;

namespace Configlue.Examples.Smoke;

// Lightweight smoke coverage for the Playground layered-configuration,
// provenance, and write-behavior scenarios. Environment input is injected
// through the source provider (no process environment mutation), and every
// file lives in an isolated temporary directory.
public sealed class LayeredSmokeTests
{
    [Test]
    public async Task LayeredSettings_EnvironmentOverridesFileWithProvenance()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "layered.json");
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PLAYGROUND__THEME"] = "EnvDark",
        };

        await using var context = ConfiglueApp.CreateContext(config =>
        {
            config.UseCommonSources(sources =>
            {
                sources.WithExplicit(path);
                sources.WithEnvironment("PLAYGROUND").EnvironmentVariables(() => variables);
                sources.Add<LayeredSettings>();
            });
        });
        var state = context.GetState<LayeredSettings>();

        // The environment layer wins for Theme; untouched members read defaults.
        (await state.GetValueAsync()).Theme.ShouldBe("EnvDark");
        (await state.GetValueAsync()).Label.ShouldBe("default");

        var details = await state.GetDetailsAsync();
        details.Theme.Value.ShouldBe("EnvDark");
        details.Theme.IsEditable.ShouldBeFalse();
        details.Theme.Editability.ShouldBe(ConfiglueEditability.Shadowed);
        details.Theme.Source?.Kind.ShouldBe("Environment");
        details.Theme.Sources.Any(static entry => entry.IsShadowed).ShouldBeTrue();
        details.Label.Value.ShouldBe("default");
    }

    [Test]
    public async Task WriteBehavior_EffectiveVsWritableDestination()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "layered.json");
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PLAYGROUND__THEME"] = "EnvDark",
        };

        await using var context = ConfiglueApp.CreateContext(config =>
        {
            config.UseCommonSources(sources =>
            {
                sources.WithExplicit(path);
                sources.WithEnvironment("PLAYGROUND").EnvironmentVariables(() => variables);
                sources.Add<LayeredSettings>();
            });
        });
        var state = context.GetState<LayeredSettings>();

        // Saving an unshadowed member lands on the writable file layer.
        await state.SaveAsync(patch =>
        {
            patch.Label = "written";
        });
        (await state.GetValueAsync()).Label.ShouldBe("written");
        (await File.ReadAllTextAsync(path)).ShouldContain("written");

        // Saving a shadowed member cannot change the effective value: the
        // higher-priority environment layer keeps winning.
        await Should.ThrowAsync<StateConflictException>(
            async () =>
                await state.SaveAsync(patch =>
                {
                    patch.Theme = "Nope";
                })
        );
        (await state.GetValueAsync()).Theme.ShouldBe("EnvDark");
    }
}
