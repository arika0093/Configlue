namespace Configlue.Tests;

public sealed class DeepCloneContractTests
{
    [Test]
    public void IConfiglueDeepCloneable_CopiesNestedModelsAndMutableCollections()
    {
        var plugins = new List<string> { "base" };
        var original = new AppSettings
        {
            Database = new DatabaseSettings { Host = "original.db" },
            Plugins = plugins,
        };
        IConfiglueDeepCloneable<AppSettings> deepCloneable = original;

        var clone = deepCloneable.DeepClone();
        plugins.Add("original-only");
        clone.Database!.Host = "clone.db";

        clone.ShouldNotBeSameAs(original);
        clone.Database.ShouldNotBeSameAs(original.Database);
        clone.Plugins.ShouldBe(["base"]);
        original.Database!.Host.ShouldBe("original.db");
    }
}
