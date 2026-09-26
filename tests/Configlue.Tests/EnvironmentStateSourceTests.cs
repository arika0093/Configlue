using Configlue;
using Configlue.Provider.Environment;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class EnvironmentStateSourceTests
{
    [Test]
    public async Task EnvironmentSource_MapsPrefixedNestedVariablesIntoSparseFragments()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__ENABLED"] = "false",
            ["APP__RETRYCOUNT"] = "0",
            ["APP__DATABASE__HOST"] = "db.example.test",
            ["OTHER__LABEL"] = "ignored",
        };
        var source = EnvironmentStateSource.FromEnvironment<AppSettings, AppSettings.Fragment>(
            "environment",
            "APP",
            priority: 100,
            environmentVariables: () => variables);

        var read = await source.Reader.ReadAsync();
        var fragment = read.Value!;

        await Assert.That(read.Status).IsEqualTo(StateReadStatus.Success);
        await Assert.That(read.Schema).IsEqualTo(AppSettings.ConfiglueSchema.ToMetadata());
        await Assert.That(fragment.Enabled.IsPresent).IsTrue();
        await Assert.That(fragment.Enabled.Value).IsFalse();
        await Assert.That(fragment.RetryCount.IsPresent).IsTrue();
        await Assert.That(fragment.RetryCount.Value).IsEqualTo(0);
        await Assert.That(fragment.Label.IsPresent).IsFalse();
        await Assert.That(fragment.Database.IsPresent).IsTrue();
        await Assert.That(fragment.Database.Value!.Host.IsPresent).IsTrue();
        await Assert.That(fragment.Database.Value.Host.Value).IsEqualTo("db.example.test");
        await Assert.That(fragment.Database.Value.Port.IsPresent).IsFalse();
        await Assert.That(source.Writer).IsNull();
        await Assert.That(source.Watcher).IsNull();
        await Assert.That(source.PhysicalOrigin).IsEqualTo("environment:APP");

        var defaults = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(true),
            RetryCount = Optional<int>.Present(5),
            Label = Optional<string?>.Present("default-label"),
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Host = Optional<string>.Present("default-db"),
                Port = Optional<int>.Present(5432),
            }),
        });
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(new StateSourceSet<AppSettings.Fragment>(
        [
            source,
            new StateSource<AppSettings.Fragment>("defaults", defaults, priority: 0),
        ]));

        var resolved = await options.ReadAsync();

        await Assert.That(resolved.Value!.Enabled).IsFalse();
        await Assert.That(resolved.Value.RetryCount).IsEqualTo(0);
        await Assert.That(resolved.Value.Label).IsEqualTo("default-label");
        await Assert.That(resolved.Value.Database!.Host).IsEqualTo("db.example.test");
        await Assert.That(resolved.Value.Database.Port).IsEqualTo(5432);
    }

    [Test]
    public async Task EnvironmentSource_UsesStableRevisionsAndReturnsNotFoundWhenNoModelKeysExist()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__UNKNOWN"] = "ignored",
            ["OTHER__ENABLED"] = "true",
        };
        var source = EnvironmentStateSource.FromEnvironment<AppSettings, AppSettings.Fragment>(
            "environment",
            "APP",
            environmentVariables: () => variables);

        var first = await source.Reader.ReadAsync();
        var second = await source.Reader.ReadAsync();
        variables["APP__UNKNOWN"] = "changed";
        var third = await source.Reader.ReadAsync();

        await Assert.That(first.Status).IsEqualTo(StateReadStatus.NotFound);
        await Assert.That(first.Revision).IsEqualTo(second.Revision);
        await Assert.That(first.Revision).IsNotEqualTo(third.Revision);
        await Assert.That(first.Revision!.Length).IsEqualTo(64);
    }

    [Test]
    public async Task EnvironmentSource_UsesCallerParserForApplicationSpecificTypes()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__RETRYCOUNT"] = "unlimited",
        };
        var source = EnvironmentStateSource.FromEnvironment<AppSettings, AppSettings.Fragment>(
            "environment",
            "APP",
            environmentVariables: () => variables,
            valueParser: (value, targetType) =>
                targetType == typeof(int) && value == "unlimited" ? int.MaxValue : throw new FormatException());

        var read = await source.Reader.ReadAsync();

        await Assert.That(read.Value!.RetryCount.Value).IsEqualTo(int.MaxValue);
    }
}
