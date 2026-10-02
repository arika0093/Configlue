using Configlue.Testing;

namespace Configlue.Tests;

public sealed class SourcePrecedenceTests
{
    [Test]
    public void StateSourceSet_OrdersHigherPriorityFirstAndKeepsRegistrationOrderForTies()
    {
        var firstTie = new StateSource<string>(
            "first-tie",
            new InMemoryStateSource<string>(),
            priority: 100
        );
        var lowest = new StateSource<string>(
            "lowest",
            new InMemoryStateSource<string>(),
            priority: 0
        );
        var secondTie = new StateSource<string>(
            "second-tie",
            new InMemoryStateSource<string>(),
            priority: 100
        );

        var sourceSet = new StateSourceSet<string>([firstTie, lowest, secondTie]);

        sourceSet
            .Sources.Select(static source => source.Id.Value)
            .ShouldBe(new[] { "first-tie", "second-tie", "lowest" });
    }

    [Test]
    public async Task Read_ResolvesHigherPriorityFirstAndEarlierRegistrationForEqualPriority()
    {
        var higher = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(10) }
        );
        var earlier = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("earlier") }
        );
        var later = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("later") }
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("earlier", earlier, priority: 100),
                new("later", later, priority: 100),
                new("higher", higher, priority: 200),
            ])
        );

        var value = await options.GetValueAsync();

        (value.RetryCount).ShouldBe(10);
        (value.Label).ShouldBe("earlier");
    }
}
