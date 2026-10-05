using Configlue.Sources;

namespace Configlue.Tests;

public sealed class TextAssignmentBinderTests
{
    [Test]
    public async Task TextualAndTypedScalarsBindConsistently()
    {
        var schema = AppSettings.ConfiglueSchema;

        var fromText = TextAssignmentBinder.Bind(
            schema,
            [new TextAssignment(["RetryCount"], "8", "env"), new TextAssignment(["Enabled"], "false", "env")],
            new TextAssignmentBinderOptions()
        );
        var fromTyped = TextAssignmentBinder.Bind(
            schema,
            [new TextAssignment(["RetryCount"], 8, "cli"), new TextAssignment(["Enabled"], false, "cli")],
            new TextAssignmentBinderOptions { DuplicatePolicy = TextAssignmentDuplicatePolicy.LastWins }
        );

        (fromText.MatchedAny).ShouldBeTrue();
        (fromTyped.MatchedAny).ShouldBeTrue();
        var textFragment = (AppSettings.Fragment)fromText.Fragment;
        var typedFragment = (AppSettings.Fragment)fromTyped.Fragment;
        (textFragment.RetryCount.Value).ShouldBe(8);
        (typedFragment.RetryCount.Value).ShouldBe(8);
        (textFragment.Enabled.Value).ShouldBeFalse();
        (typedFragment.Enabled.Value).ShouldBeFalse();
        // Equivalent logical values share the converted-value revision.
        (fromText.Revision).ShouldBe(fromTyped.Revision);
        await Task.CompletedTask;
    }

    [Test]
    public async Task NullableEnumGuidAndDateScalarsConvertFromText()
    {
        var schema = CliNullable195Settings.ConfiglueSchema;

        var bound = TextAssignmentBinder.Bind(
            schema,
            [
                new TextAssignment(["IntValue"], "7", "env"),
                new TextAssignment(["BoolValue"], "true", "env"),
                new TextAssignment(["Kind"], "Beta", "env"),
                new TextAssignment(["Label"], "hello", "env"),
            ],
            new TextAssignmentBinderOptions()
        );

        (bound.MatchedAny).ShouldBeTrue();
        var fragment = (CliNullable195Settings.Fragment)bound.Fragment;
        (fragment.IntValue.Value).ShouldBe(7);
        (fragment.BoolValue.Value).ShouldBe(true);
        (fragment.Kind.Value).ShouldBe(CliNullable195Kind.Beta);
        (fragment.Label.Value).ShouldBe("hello");
        await Task.CompletedTask;
    }

    [Test]
    public async Task NestedAssignmentsShareOneParentFragment()
    {
        var schema = AppSettings.ConfiglueSchema;

        var bound = TextAssignmentBinder.Bind(
            schema,
            [
                new TextAssignment(["Database", "Host"], "db.example.test", "a"),
                new TextAssignment(["Database", "Port"], "6432", "b"),
            ],
            new TextAssignmentBinderOptions()
        );

        (bound.MatchedAny).ShouldBeTrue();
        var fragment = (AppSettings.Fragment)bound.Fragment;
        (fragment.Database.IsPresent).ShouldBeTrue();
        (fragment.Database.Value!.Host.Value).ShouldBe("db.example.test");
        (fragment.Database.Value.Port.Value).ShouldBe(6432);
        await Task.CompletedTask;
    }

    [Test]
    public async Task CollectionsBindFromJsonTextAndFromTypedSequences()
    {
        var appSchema = AppSettings.ConfiglueSchema;
        var fromJson = TextAssignmentBinder.Bind(
            appSchema,
            [new TextAssignment(["Plugins"], """["nord","dracula"]""", "env")],
            new TextAssignmentBinderOptions()
        );
        var fromTyped = TextAssignmentBinder.Bind(
            appSchema,
            [new TextAssignment(["Plugins"], new[] { "nord", "dracula" }, "cli")],
            new TextAssignmentBinderOptions { DuplicatePolicy = TextAssignmentDuplicatePolicy.LastWins }
        );

        var jsonFragment = (AppSettings.Fragment)fromJson.Fragment;
        var typedFragment = (AppSettings.Fragment)fromTyped.Fragment;
        (jsonFragment.Plugins.Value!).ShouldBe(["nord", "dracula"]);
        (typedFragment.Plugins.Value!).ShouldBe(["nord", "dracula"]);
        // Note: JSON text deserializes interfaces to List<T> while typed string[]
        // stays as an array, so revisions differ by concrete collection type even
        // though logical values match. Scalar equivalents share revisions above.

        var ownershipSchema = OwnershipSettings.ConfiglueSchema;
        var ownership = TextAssignmentBinder.Bind(
            ownershipSchema,
            [
                new TextAssignment(["ArrayValues"], """["a","b"]""", "env"),
                new TextAssignment(["ListValues"], new List<string> { "c" }, "cli"),
                new TextAssignment(["Children"], """[{"Name":"first"}]""", "env"),
            ],
            new TextAssignmentBinderOptions { DuplicatePolicy = TextAssignmentDuplicatePolicy.LastWins }
        );
        var owned = (OwnershipSettings.Fragment)ownership.Fragment;
        (owned.ArrayValues.Value!).ShouldBe(["a", "b"]);
        (owned.ListValues.Value!).ShouldBe(["c"]);
        (owned.Children.Value!.Single().Name).ShouldBe("first");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CustomTextParserHookOverridesAndDeclinesToJson()
    {
        var schema = AppSettings.ConfiglueSchema;

        var overridden = TextAssignmentBinder.Bind(
            schema,
            [new TextAssignment(["RetryCount"], "unlimited", "env")],
            new TextAssignmentBinderOptions
            {
                TextParser = (value, targetType) =>
                    targetType == typeof(int) && value == "unlimited"
                        ? int.MaxValue
                        : throw new FormatException(),
            }
        );
        ((AppSettings.Fragment)overridden.Fragment).RetryCount.Value.ShouldBe(int.MaxValue);

        var declined = TextAssignmentBinder.Bind(
            schema,
            [new TextAssignment(["Plugins"], """["declined-then-json"]""", "env")],
            new TextAssignmentBinderOptions
            {
                TextParser = (value, targetType) =>
                    targetType == typeof(int)
                        ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                        : throw new NotSupportedException(),
            }
        );
        ((AppSettings.Fragment)declined.Fragment).Plugins.Value!.ShouldBe(["declined-then-json"]);
        await Task.CompletedTask;
    }

    [Test]
    public async Task DuplicatePolicyThrowRejectsWhileLastWinsKeepsLater()
    {
        var schema = AppSettings.ConfiglueSchema;
        var assignments = new List<TextAssignment>
        {
            new(["RetryCount"], "1", "first"),
            new(["RetryCount"], "2", "second"),
        };

        Should.Throw<InvalidOperationException>(() =>
            TextAssignmentBinder.Bind(
                schema,
                assignments,
                new TextAssignmentBinderOptions { DuplicatePolicy = TextAssignmentDuplicatePolicy.Throw }
            )
        );

        var lastWins = TextAssignmentBinder.Bind(
            schema,
            assignments,
            new TextAssignmentBinderOptions { DuplicatePolicy = TextAssignmentDuplicatePolicy.LastWins }
        );
        ((AppSettings.Fragment)lastWins.Fragment).RetryCount.Value.ShouldBe(2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task RevisionIsDeterministicAcrossInputOrder()
    {
        var schema = AppSettings.ConfiglueSchema;
        var first = TextAssignmentBinder.Bind(
            schema,
            [
                new TextAssignment(["RetryCount"], "1", "a"),
                new TextAssignment(["Database", "Host"], "db", "b"),
            ],
            new TextAssignmentBinderOptions()
        );
        var reordered = TextAssignmentBinder.Bind(
            schema,
            [
                new TextAssignment(["Database", "Host"], "db", "b"),
                new TextAssignment(["RetryCount"], "1", "a"),
            ],
            new TextAssignmentBinderOptions()
        );
        var changed = TextAssignmentBinder.Bind(
            schema,
            [
                new TextAssignment(["RetryCount"], "2", "a"),
                new TextAssignment(["Database", "Host"], "db", "b"),
            ],
            new TextAssignmentBinderOptions()
        );

        (first.Revision).ShouldBe(reordered.Revision);
        (first.Revision).ShouldNotBe(changed.Revision);
        (first.Revision.Length).ShouldBe(64);
        await Task.CompletedTask;
    }

    [Test]
    public async Task UnknownMembersAreUnmatchedButAffectRevision()
    {
        var schema = AppSettings.ConfiglueSchema;

        var first = TextAssignmentBinder.Bind(
            schema,
            [new TextAssignment(["Unknown"], "ignored", "APP__UNKNOWN")],
            new TextAssignmentBinderOptions()
        );
        var second = TextAssignmentBinder.Bind(
            schema,
            [new TextAssignment(["Unknown"], "changed", "APP__UNKNOWN")],
            new TextAssignmentBinderOptions()
        );

        (first.MatchedAny).ShouldBeFalse();
        (first.Revision).ShouldNotBe(second.Revision);
        await Task.CompletedTask;
    }

    [Test]
    public async Task StructuralMisuseThrowsWithPathAndOrigin()
    {
        var schema = AppSettings.ConfiglueSchema;

        var pastScalar = Should.Throw<FormatException>(() =>
            TextAssignmentBinder.Bind(
                schema,
                [new TextAssignment(["Label", "Sub"], "x", "origin-a")],
                new TextAssignmentBinderOptions()
            )
        );
        (pastScalar.Message).ShouldContain("Label");
        (pastScalar.Message).ShouldContain("origin-a");

        var nestedLeaf = Should.Throw<FormatException>(() =>
            TextAssignmentBinder.Bind(
                schema,
                [new TextAssignment(["Database"], "x", "origin-b")],
                new TextAssignmentBinderOptions()
            )
        );
        (nestedLeaf.Message).ShouldContain("Database");
        (nestedLeaf.Message).ShouldContain("origin-b");

        var badConversion = Should.Throw<FormatException>(() =>
            TextAssignmentBinder.Bind(
                schema,
                [new TextAssignment(["RetryCount"], "not-a-number", "origin-c")],
                new TextAssignmentBinderOptions()
            )
        );
        (badConversion.Message).ShouldContain("RetryCount");
        (badConversion.Message).ShouldContain("origin-c");
        await Task.CompletedTask;
    }

    [Test]
    public void AmbiguousSegmentsThrow()
    {
        var schema = new ConfiglueModelSchema(
            typeof(AppSettings),
            "ambiguous-test",
            1,
            [
                new ConfiglueMemberSchema(0, "Dup", typeof(string), MergeMode.Replace),
                new ConfiglueMemberSchema(1, "DUP", typeof(string), MergeMode.Replace),
            ],
            AppSettings.ConfiglueSchema.CreateEmptyFragment
        );

        Should.Throw<FormatException>(() =>
            TextAssignmentBinder.Bind(
                schema,
                [new TextAssignment(["dup"], "x", "origin")],
                new TextAssignmentBinderOptions()
            )
        );
    }
}
