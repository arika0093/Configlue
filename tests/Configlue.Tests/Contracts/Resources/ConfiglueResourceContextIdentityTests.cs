using Configlue.Resources;

namespace Configlue.Tests;

public sealed class ConfiglueResourceContextIdentityTests
{
    [Test]
    public void EqualityUsesStableModelSubjectKeyResourceKeyAndRouteTuple()
    {
        var first = new ConfiglueResourceContext(
            "model-a",
            new SubjectA("tenant"),
            ResourceKey.From("record"),
            RouteKey.From("west")
        );
        var equalByKeyAndType = new ConfiglueResourceContext(
            "model-a",
            new SubjectA("tenant"),
            ResourceKey.From("record"),
            RouteKey.From("west")
        );
        var equalByKeyAcrossTypes = new ConfiglueResourceContext(
            "model-a",
            new SubjectB("tenant"),
            ResourceKey.From("record"),
            RouteKey.From("west")
        );
        var differentModel = new ConfiglueResourceContext(
            "model-b",
            new SubjectA("tenant"),
            ResourceKey.From("record"),
            RouteKey.From("west")
        );
        var differentSubject = new ConfiglueResourceContext(
            "model-a",
            new SubjectB("other"),
            ResourceKey.From("record"),
            RouteKey.From("west")
        );
        var differentResource = new ConfiglueResourceContext(
            "model-a",
            new SubjectB("tenant"),
            ResourceKey.From("other"),
            RouteKey.From("west")
        );
        var differentRoute = new ConfiglueResourceContext(
            "model-a",
            new SubjectB("tenant"),
            ResourceKey.From("record"),
            RouteKey.From("east")
        );

        first.ShouldBe(equalByKeyAndType);
        first.ShouldBe(equalByKeyAcrossTypes);
        first.GetHashCode().ShouldBe(equalByKeyAcrossTypes.GetHashCode());
        new HashSet<ConfiglueResourceContext> { first }
            .Contains(equalByKeyAcrossTypes)
            .ShouldBeTrue();
        new Dictionary<ConfiglueResourceContext, string> { [first] = "found" }[
            equalByKeyAcrossTypes
        ]
            .ShouldBe("found");
        first.ShouldNotBe(differentModel);
        first.ShouldNotBe(differentSubject);
        first.ShouldNotBe(differentResource);
        first.ShouldNotBe(differentRoute);
    }

    [Test]
    public void DefaultAndZeroInitializedContextsHaveEqualIdentity()
    {
        default(ConfiglueResourceContext).ShouldBe(ConfiglueResourceContext.Default);
        default(ConfiglueResourceContext)
            .GetHashCode()
            .ShouldBe(ConfiglueResourceContext.Default.GetHashCode());
    }

    private sealed record SubjectA(string Value) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Value);
    }

    private sealed record SubjectB(string Value) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Value);
    }
}
