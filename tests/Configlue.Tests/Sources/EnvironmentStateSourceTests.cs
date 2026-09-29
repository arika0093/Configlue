using Configlue;
using Configlue.Source.Environment;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class EnvironmentStateSourceTests
{
    [Test]
    public async Task EnvironmentSource_MapsExplicitPropertyAndNestedVariableNames()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP_SETTINGS_LABEL"] = "configured-label",
            ["DATABASE_HOST"] = "configured-db",
        };
        var source = EnvironmentStateSource.FromEnvironment<AppSettings, AppSettings.Fragment>(
            "environment",
            "APP",
            environmentVariables: () => variables
        );

        var read = await source.Reader.ReadAsync();

        (read.Status).ShouldBe(StateReadStatus.Success);
        (read.Value!.Label.IsPresent).ShouldBeTrue();
        (read.Value.Label.Value).ShouldBe("configured-label");
        (read.Value.Database.IsPresent).ShouldBeTrue();
        (read.Value.Database.Value!.Host.Value).ShouldBe("configured-db");
    }

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
            environmentVariables: () => variables
        );

        var read = await source.Reader.ReadAsync();
        var fragment = read.Value!;

        (read.Status).ShouldBe(StateReadStatus.Success);
        (read.Schema).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
        (fragment.Enabled.IsPresent).ShouldBeTrue();
        (fragment.Enabled.Value).ShouldBeFalse();
        (fragment.RetryCount.IsPresent).ShouldBeTrue();
        (fragment.RetryCount.Value).ShouldBe(0);
        (fragment.Label.IsPresent).ShouldBeFalse();
        (fragment.Database.IsPresent).ShouldBeTrue();
        (fragment.Database.Value!.Host.IsPresent).ShouldBeTrue();
        (fragment.Database.Value.Host.Value).ShouldBe("db.example.test");
        (fragment.Database.Value.Port.IsPresent).ShouldBeFalse();
        (source.Writer).ShouldBeNull();
        (source.Watcher).ShouldBeNull();
        (source.PhysicalOrigin).ShouldBe("environment:APP");

        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Enabled = Optional<bool>.Present(true),
                RetryCount = Optional<int>.Present(5),
                Label = Optional<string?>.Present("default-label"),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("default-db"),
                        Port = Optional<int>.Present(5432),
                    }
                ),
            }
        );
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                source,
                new StateSource<AppSettings.Fragment>("defaults", defaults, priority: 0),
            ])
        );

        var resolved = await options.ReadAsync();

        (resolved.Value!.Enabled).ShouldBeFalse();
        (resolved.Value.RetryCount).ShouldBe(0);
        (resolved.Value.Label).ShouldBe("default-label");
        (resolved.Value.Database!.Host).ShouldBe("db.example.test");
        (resolved.Value.Database.Port).ShouldBe(5432);
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
            environmentVariables: () => variables
        );

        var first = await source.Reader.ReadAsync();
        var second = await source.Reader.ReadAsync();
        variables["APP__UNKNOWN"] = "changed";
        var third = await source.Reader.ReadAsync();

        (first.Status).ShouldBe(StateReadStatus.NotFound);
        (first.Revision).ShouldBe(second.Revision);
        (first.Revision).ShouldNotBe(third.Revision);
        (first.Revision!.Length).ShouldBe(64);
    }

    [Test]
    public async Task EnvironmentSource_RevisionMatchesLegacyDeterministicEncoding()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__UNKNOWN"] = "ignored",
            ["OTHER__ENABLED"] = "true",
        };
        var source = EnvironmentStateSource.FromEnvironment<AppSettings, AppSettings.Fragment>(
            "environment",
            "APP",
            environmentVariables: () => variables
        );

        var read = await source.Reader.ReadAsync();

        // SHA256 of the length-prefixed, upper-cased "12:APP__UNKNOWN7:ignored" payload.
        (read.Revision).ShouldBe(
            "F93D39285281CD8BC940C179D5FBCCA53F73DF6D26EF6F45428B9C2B2358A498"
        );
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
                targetType == typeof(int) && value == "unlimited"
                    ? int.MaxValue
                    : throw new FormatException()
        );

        var read = await source.Reader.ReadAsync();

        (read.Value!.RetryCount.Value).ShouldBe(int.MaxValue);
    }

    [Test]
    public async Task EnvironmentSource_InterpretsCollectionMembersAsJson()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__PLUGINS"] = """["nord","dracula"]""",
        };
        var source = EnvironmentStateSource.FromEnvironment<AppSettings, AppSettings.Fragment>(
            "environment",
            "APP",
            environmentVariables: () => variables
        );

        var read = await source.Reader.ReadAsync();

        (read.Status).ShouldBe(StateReadStatus.Success);
        (read.Value!.Plugins.IsPresent).ShouldBeTrue();
        (read.Value.Plugins.Value!).ShouldBe(["nord", "dracula"]);
    }

    [Test]
    public async Task EnvironmentSource_InterpretsArraysListsSetsAndObjectsAsJson()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__ARRAYVALUES"] = """["a","b"]""",
            ["APP__LISTVALUES"] = """["c"]""",
            ["APP__SETVALUES"] = """["x","y","x"]""",
            ["APP__CHILDREN"] = """[{"Name":"first"},{"Name":"second"}]""",
        };
        var source = EnvironmentStateSource.FromEnvironment<
            OwnershipSettings,
            OwnershipSettings.Fragment
        >("environment", "APP", environmentVariables: () => variables);

        var read = await source.Reader.ReadAsync();

        (read.Status).ShouldBe(StateReadStatus.Success);
        (read.Value!.ArrayValues.Value!).ShouldBe(["a", "b"]);
        (read.Value.ListValues.Value!).ShouldBe(["c"]);
        (read.Value.SetValues.Value!).ShouldBe(["x", "y"], ignoreOrder: true);
        (read.Value.Children.Value!.Select(static child => child.Name)).ShouldBe([
            "first",
            "second",
        ]);
    }

    [Test]
    public async Task EnvironmentSource_RejectsInvalidJsonForCollectionMembers()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__PLUGINS"] = "not-json",
        };
        var source = EnvironmentStateSource.FromEnvironment<AppSettings, AppSettings.Fragment>(
            "environment",
            "APP",
            environmentVariables: () => variables
        );

        await Should.ThrowAsync<FormatException>(async () => await source.Reader.ReadAsync());
    }

    [Test]
    public async Task EnvironmentSource_FallsBackToJsonWhenACustomParserDeclines()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__RETRYCOUNT"] = "7",
            ["APP__PLUGINS"] = """["declined-then-json"]""",
        };
        var source = EnvironmentStateSource.FromEnvironment<AppSettings, AppSettings.Fragment>(
            "environment",
            "APP",
            environmentVariables: () => variables,
            valueParser: (value, targetType) =>
                targetType == typeof(int)
                    ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                    : throw new NotSupportedException()
        );

        var read = await source.Reader.ReadAsync();

        (read.Status).ShouldBe(StateReadStatus.Success);
        (read.Value!.RetryCount.Value).ShouldBe(7);
        (read.Value.Plugins.Value!).ShouldBe(["declined-then-json"]);
    }

    [Test]
    public async Task EnvironmentSource_HonorsCustomJsonOptions()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__CHILDREN"] = """[{"name":"lower"}]""",
        };
        var source = EnvironmentStateSource.FromEnvironment<
            OwnershipSettings,
            OwnershipSettings.Fragment
        >(
            "environment",
            "APP",
            environmentVariables: () => variables,
            jsonSerializerOptions: new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }
        );

        var read = await source.Reader.ReadAsync();

        (read.Status).ShouldBe(StateReadStatus.Success);
        (read.Value!.Children.Value!.Single().Name).ShouldBe("lower");
    }
}
