using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed class StateCapabilityTests
{
    [Test]
    public async Task FocusedCapabilitiesShareTheRegisteredRuntimeForDefaultAndNamedStates()
    {
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "default",
                            new InMemoryStateSource<AppSettings.Fragment>()
                        )
                    )
                )
            );
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "named";
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "named",
                            new InMemoryStateSource<AppSettings.Fragment>()
                        )
                    )
                );
            });
        });
        await using var provider = services.BuildServiceProvider();
        var context = provider.GetRequiredService<ConfiglueContext>();
        var options = context.GetState<AppSettings>();
        ReferenceEquals(options, provider.GetRequiredService<IConfiglueInspection<AppSettings>>())
            .ShouldBeTrue();
        ReferenceEquals(options, provider.GetRequiredService<IConfiglueEditSessions<AppSettings>>())
            .ShouldBeTrue();
        ReferenceEquals(options, provider.GetRequiredService<IConfiglueSources<AppSettings>>())
            .ShouldBeTrue();
        ReferenceEquals(options, provider.GetRequiredService<IConfiglueDiagnostics<AppSettings>>())
            .ShouldBeTrue();
        var named = context.GetState<AppSettings>("named");
        ReferenceEquals(
                named,
                provider.GetRequiredKeyedService<IConfiglueInspection<AppSettings>>("named")
            )
            .ShouldBeTrue();
        ReferenceEquals(
                named,
                provider.GetRequiredKeyedService<IConfiglueEditSessions<AppSettings>>("named")
            )
            .ShouldBeTrue();
        ReferenceEquals(
                named,
                provider.GetRequiredKeyedService<IConfiglueSources<AppSettings>>("named")
            )
            .ShouldBeTrue();
        ReferenceEquals(
                named,
                provider.GetRequiredKeyedService<IConfiglueDiagnostics<AppSettings>>("named")
            )
            .ShouldBeTrue();
        (await context.GetInspection<AppSettings>().Check().Result).Status.ShouldBe(
            ConfiglueCheckStatus.Success
        );
    }
}
