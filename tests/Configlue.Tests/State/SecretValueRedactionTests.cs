using System.ComponentModel.DataAnnotations;
using Configlue;
using Configlue.CompilerServices;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

[ConfiglueModel("secret-value-basic", Version = 1)]
public partial class SecretBasicSettings
{
    public string Host { get; set; } = "localhost";

    [SecretValue]
    public string Password { get; set; } = "";
}

[ConfiglueModel("secret-value-nested", Version = 1)]
public partial class SecretNestedSettings
{
    [SecretValue]
    public SecretDatabase? Database { get; set; } = new();

    public string Name { get; set; } = "";
}

public sealed class SecretDatabase
{
    public string Host { get; set; } = "localhost";

    public string Password { get; set; } = "d3fault-secret";
}

[ConfiglueModel("secret-value-leaf", Version = 1)]
public partial class SecretLeafSettings
{
    public LeafInner? Inner { get; set; } = new();
}

public sealed class LeafInner
{
    public string Name { get; set; } = "";

    [SecretValue]
    public string Token { get; set; } = "";
}

[ConfiglueModel("secret-value-collections", Version = 1)]
public partial class SecretCollectionSettings
{
    [SecretValue]
    [ConfiglueMerge(MergeMode.Append)]
    public IReadOnlyList<string> Tokens { get; set; } = [];

    [ConfiglueMerge(MergeMode.Append)]
    public IReadOnlyList<string> Tags { get; set; } = [];
}

[ConfiglueModel("secret-value-plain", Version = 1)]
public partial class PlainSecretProbeSettings
{
    public string Name { get; set; } = "";
}

[ConfiglueModel("secret-value-validation", Version = 1)]
public partial class SecretValidationSettings
{
    [SecretValue]
    [EchoSecret]
    public string Password { get; set; } = "";
}

public sealed class EchoSecretAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(
        object? value,
        ValidationContext validationContext
    ) => new ValidationResult($"Rejected '{value}' for {validationContext.MemberName}.");
}

public sealed class SecretValueRedactionTests
{
    [Test]
    public void SchemaFlag_MarksSecretMembers()
    {
        var members = SecretBasicSettings.ConfiglueSchema.Members.ToDictionary(static member =>
            member.Name
        );
        (members["Password"].IsSecret).ShouldBeTrue();
        (members["Host"].IsSecret).ShouldBeFalse();
    }

    [Test]
    public void SchemaFlag_FragmentSchemaMatches()
    {
        var members = SecretBasicSettings.FragmentSchema.Members.ToDictionary(static member =>
            member.Name
        );
        (members["Password"].IsSecret).ShouldBeTrue();
        (members["Host"].IsSecret).ShouldBeFalse();
    }

    [Test]
    public void SchemaFlag_PlainModelRemainsUnchanged()
    {
        foreach (var member in PlainSecretProbeSettings.ConfiglueSchema.Members)
        {
            (member.IsSecret).ShouldBeFalse();
        }

        var details = new ConfigValueDetails<string>(
            "visible",
            ConfiglueEditability.Editable,
            null,
            []
        );
        (details.IsSecret).ShouldBeFalse();
        (details.ToString()).ShouldBe("visible");
    }

    [Test]
    public void NestedSecret_WholeSubtreeIsSensitive()
    {
        var schema = SecretNestedSettings.ConfiglueSchema;
        var database = schema.Members.Single(member => member.Name == "Database");
        (database.IsSecret).ShouldBeTrue();

        var nested = database.NestedSchemaFactory!();
        var password = nested.Members.Single(member => member.Name == "Password");
        var path = ConfiglueMemberPath.Root(schema).Append(database.Id).Append(password.Id);
        (path.IsSecret()).ShouldBeTrue();
        (ConfiglueSecrets.IsSensitive(path)).ShouldBeTrue();

        var name = schema.Members.Single(member => member.Name == "Name");
        var namePath = ConfiglueMemberPath.Root(schema).Append(name.Id);
        (namePath.IsSecret()).ShouldBeFalse();
    }

    [Test]
    public void NestedLeaf_SecretLeafPropagatesThroughNonSecretParent()
    {
        var schema = SecretLeafSettings.ConfiglueSchema;
        var inner = schema.Members.Single(member => member.Name == "Inner");
        (inner.IsSecret).ShouldBeFalse();

        var nested = inner.NestedSchemaFactory!();
        var token = nested.Members.Single(member => member.Name == "Token");
        (token.IsSecret).ShouldBeTrue();

        var tokenPath = ConfiglueMemberPath.Root(schema).Append(inner.Id).Append(token.Id);
        (tokenPath.IsSecret()).ShouldBeTrue();

        var name = nested.Members.Single(member => member.Name == "Name");
        var namePath = ConfiglueMemberPath.Root(schema).Append(inner.Id).Append(name.Id);
        (namePath.IsSecret()).ShouldBeFalse();
    }

    [Test]
    public void CollectionSecret_MarksCollectionMember()
    {
        var members = SecretCollectionSettings.ConfiglueSchema.Members.ToDictionary(static member =>
            member.Name
        );
        (members["Tokens"].IsSecret).ShouldBeTrue();
        (members["Tags"].IsSecret).ShouldBeFalse();
    }

    [Test]
    public async Task TypedValue_ReturnsRealValueWhileToStringRedacts()
    {
        await using var options = new ConfiglueRuntime<
            SecretBasicSettings,
            SecretBasicSettings.Fragment
        >(
            new StateSourceSet<SecretBasicSettings.Fragment>([
                new StateSource<SecretBasicSettings.Fragment>("local", new InMemoryStateSource<SecretBasicSettings.Fragment>(
                        new SecretBasicSettings.Fragment
                        {
                            Host = Optional<string>.Present("db.local"),
                            Password = Optional<string>.Present("s3cr3t-value"),
                        }
                    ), new StateSourceOptions<SecretBasicSettings.Fragment>()),
            ])
        );

        var details = await options.GetDetailsAsync();

        (details.Password.Value).ShouldBe("s3cr3t-value");
        string? typed = details.Password;
        (typed).ShouldBe("s3cr3t-value");
        (details.Password.IsSecret).ShouldBeTrue();
        (details.Password.ToString()).ShouldBe(ConfiglueSecrets.RedactedText);
        (details.Password.ToString().Contains("s3cr3t-value")).ShouldBeFalse();
        (details.Host.Value).ShouldBe("db.local");
        (details.Host.IsSecret).ShouldBeFalse();
        (details.Host.ToString()).ShouldBe("db.local");
    }

    [Test]
    public async Task PerSourceContribution_RedactsDisplayButKeepsValue()
    {
        await using var options = new ConfiglueRuntime<
            SecretBasicSettings,
            SecretBasicSettings.Fragment
        >(
            new StateSourceSet<SecretBasicSettings.Fragment>([
                new StateSource<SecretBasicSettings.Fragment>("local", new InMemoryStateSource<SecretBasicSettings.Fragment>(
                        new SecretBasicSettings.Fragment
                        {
                            Password = Optional<string>.Present("s3cr3t-value"),
                        }
                    ), new StateSourceOptions<SecretBasicSettings.Fragment>()),
            ])
        );

        var details = await options.GetDetailsAsync();
        var contribution = details.Password.Sources[0];
        (contribution.IsPresent).ShouldBeTrue();

        (contribution.Value).ShouldBe("s3cr3t-value");
        (contribution.IsSecret).ShouldBeTrue();
        (contribution.ToString().Contains("s3cr3t-value")).ShouldBeFalse();
        (contribution.ToString().Contains(ConfiglueSecrets.RedactedText)).ShouldBeTrue();
    }

    [Test]
    public async Task NestedDetails_RedactSecretSubtreeLeaves()
    {
        await using var options = new ConfiglueRuntime<
            SecretNestedSettings,
            SecretNestedSettings.Fragment
        >(
            new StateSourceSet<SecretNestedSettings.Fragment>([
                new StateSource<SecretNestedSettings.Fragment>("local", new InMemoryStateSource<SecretNestedSettings.Fragment>(
                        new SecretNestedSettings.Fragment
                        {
                            Name = Optional<string>.Present("primary"),
                        }
                    ), new StateSourceOptions<SecretNestedSettings.Fragment>()),
            ])
        );

        var details = await options.GetDetailsAsync();

        details.Database.ShouldNotBeNull();
        (details.Database!.Password.Value).ShouldBe("d3fault-secret");
        (details.Database!.Password.IsSecret).ShouldBeTrue();
        (details.Database!.Password.ToString()).ShouldBe(ConfiglueSecrets.RedactedText);
        (details.Database!.Password.ToString().Contains("d3fault-secret")).ShouldBeFalse();
        (details.Name.IsSecret).ShouldBeFalse();
        (details.Name.ToString()).ShouldBe("primary");
    }

    [Test]
    public async Task CollectionDetails_RedactSecretElements()
    {
        await using var options = new ConfiglueRuntime<
            SecretCollectionSettings,
            SecretCollectionSettings.Fragment
        >(
            new StateSourceSet<SecretCollectionSettings.Fragment>([
                new StateSource<SecretCollectionSettings.Fragment>("local", new InMemoryStateSource<SecretCollectionSettings.Fragment>(
                        new SecretCollectionSettings.Fragment
                        {
                            Tokens = Optional<IReadOnlyList<string>>.Present(["token-a"]),
                            Tags = Optional<IReadOnlyList<string>>.Present(["tag-a"]),
                        }
                    ), new StateSourceOptions<SecretCollectionSettings.Fragment>()),
            ])
        );

        var details = await options.GetDetailsAsync();

        details.Tokens.ShouldNotBeNull();
        details.Tags.ShouldNotBeNull();
        (details.Tokens!.IsSecret).ShouldBeTrue();
        (details.Tags!.IsSecret).ShouldBeFalse();
        (details.Tokens.Value).ShouldBe(["token-a"]);
        foreach (var element in details.Tokens.Elements)
        {
            (element.IsSecret).ShouldBeTrue();
            (element.Value).ShouldBe("token-a");
            (element.ToString()).ShouldBe(ConfiglueSecrets.RedactedText);
            foreach (var contribution in element.Contributions)
            {
                (contribution.Value).ShouldBe("token-a");
                (contribution.ToString().Contains("token-a")).ShouldBeFalse();
            }
        }

        foreach (var element in details.Tags!.Elements)
        {
            (element.IsSecret).ShouldBeFalse();
            (element.ToString()).ShouldBe("tag-a");
        }
    }

    [Test]
    public void CentralHelper_FormatsValuesConsistently()
    {
        (ConfiglueSecrets.FormatValue("s3cr3t", true)).ShouldBe(ConfiglueSecrets.RedactedText);
        (ConfiglueSecrets.FormatValue("visible", false)).ShouldBe("visible");
        (ConfiglueSecrets.FormatValue(null, false)).ShouldBe(string.Empty);
        (ConfiglueSecrets.FormatContribution("s3cr3t", true, true)).ShouldBe(
            ConfiglueSecrets.RedactedText
        );
        (ConfiglueSecrets.FormatContribution(null, true, false)).ShouldBe(string.Empty);
        (ConfiglueSecrets.JsonSchemaExtensionName).ShouldBe("x-configlue-secret");
    }

    [Test]
    public void ValidationMessage_RedactsSecretPlaintext()
    {
        var redacted = ConfiglueSecrets.RedactMessage(
            "Rejected 's3cr3t-value' for Password.",
            "s3cr3t-value",
            true
        );
        (redacted.Contains("s3cr3t-value")).ShouldBeFalse();
        (redacted.Contains(ConfiglueSecrets.RedactedText)).ShouldBeTrue();

        var untouched = ConfiglueSecrets.RedactMessage(
            "Rejected 'visible' for Name.",
            "visible",
            false
        );
        (untouched).ShouldBe("Rejected 'visible' for Name.");
    }

    [Test]
    public async Task ValidationFailure_DoesNotExposeSecretValue()
    {
        await using var options = new ConfiglueRuntime<
            SecretValidationSettings,
            SecretValidationSettings.Fragment
        >(
            new StateSourceSet<SecretValidationSettings.Fragment>([
                new StateSource<SecretValidationSettings.Fragment>("local", new InMemoryStateSource<SecretValidationSettings.Fragment>(
                        new SecretValidationSettings.Fragment
                        {
                            Password = Optional<string>.Present("s3cr3t-value"),
                        }
                    ), new StateSourceOptions<SecretValidationSettings.Fragment>()),
            ])
        );

        var exception = await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await options.GetValueAsync()
        );
        var joined = string.Join("; ", exception.Failures);
        (joined.Contains("s3cr3t-value")).ShouldBeFalse();
        (exception.Message.Contains("s3cr3t-value")).ShouldBeFalse();
    }

    [Test]
    public async Task DiagnosticEvents_DoNotCarrySecretValues()
    {
        await using var options = new ConfiglueRuntime<
            SecretBasicSettings,
            SecretBasicSettings.Fragment
        >(
            new StateSourceSet<SecretBasicSettings.Fragment>([
                new StateSource<SecretBasicSettings.Fragment>("local", new InMemoryStateSource<SecretBasicSettings.Fragment>(
                        new SecretBasicSettings.Fragment
                        {
                            Password = Optional<string>.Present("s3cr3t-value"),
                        }
                    ), new StateSourceOptions<SecretBasicSettings.Fragment>()),
            ])
        );

        _ = await options.GetValueAsync();
        var events = options.GetRecentEvents();
        foreach (var diagnosticEvent in events)
        {
            (diagnosticEvent.ToString()!.Contains("s3cr3t-value")).ShouldBeFalse();
        }

        var snapshot = options.GetRuntimeSnapshot();
        (snapshot.ToString()!.Contains("s3cr3t-value")).ShouldBeFalse();
    }

    [Test]
    public void SensitivityAvailable_WithoutRuntimeReflection()
    {
        var schema = SecretBasicSettings.ConfiglueSchema;
        var password = schema.Members.Single(member => member.Name == "Password");
        (password.IsSecret).ShouldBeTrue();

        var path = ConfiglueMemberPath.Root(schema).Append(password.Id);
        (path.IsSecret()).ShouldBeTrue();

        var attribute = typeof(SecretBasicSettings)
            .GetProperty(nameof(SecretBasicSettings.Password))!
            .GetCustomAttributes(typeof(SecretValueAttribute), inherit: true);
        (attribute.Length).ShouldBe(1);
    }
}
