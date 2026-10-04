using Configlue;
using Configlue.Testing;

namespace Configlue.Tests;

[ConfiglueModel("required-construction")]
public partial class RequiredConstructionSettings
{
    private static int _nextIdentity;

    private RequiredConstructionSettings() => Identity = Interlocked.Increment(ref _nextIdentity);

    public required int Identity { get; init; }
    public required string Name { get; init; } = "fallback";
}

public sealed class RequiredConstructionTests
{
    [Test]
    public void RequiredProjectionPreservesDefaultsAndExplicitValues()
    {
        var missing = RequiredConstructionSettings.Fragment.Empty.ToModel();
        missing.Identity.ShouldBeGreaterThan(0);
        missing.Name.ShouldBe("fallback");
        var projected = new RequiredConstructionSettings.Fragment
        {
            Identity = Optional<int>.Present(0),
            Name = Optional<string>.Present("configured"),
        }.ToModel();
        projected.Identity.ShouldBe(0);
        projected.Name.ShouldBe("configured");
        projected.DeepClone().Identity.ShouldBe(0);
    }

    [Test]
    public async Task RuntimeDefaultsRemainStableAcrossReads()
    {
        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.Add<RequiredConstructionSettings>(model =>
                model.ConfigureSources(registration =>
                    registration.Sources.Add(
                        _ => new StateSource<RequiredConstructionSettings.Fragment>("missing", new InMemoryStateSource<RequiredConstructionSettings.Fragment>(), new StateSourceOptions<RequiredConstructionSettings.Fragment>())
                    )
                )
            )
        );
        var state = context.GetState<RequiredConstructionSettings>();
        var first = await state.GetValueAsync();
        var second = await state.GetValueAsync();

        first.Identity.ShouldBeGreaterThan(0);
        second.Identity.ShouldBe(first.Identity);
        second.Name.ShouldBe("fallback");
    }
}
