using System.ComponentModel.DataAnnotations;
using Configlue;

namespace Configlue.JsonSchema.MSBuild.Fixtures;

[ConfiglueModel("fixture.settings", Version = 3)]
public partial class FixtureSettings
{
    [Range(1, 1000)]
    public int MaxConnections { get; set; }

    [Required]
    [MinLength(3)]
    public string Name { get; set; } = "";

    [DataType(DataType.Date)]
    public string PublishedDate { get; set; } = "";

    public NestedFixture Nested { get; set; } = new();
}

[ConfiglueModel("fixture.second")]
public partial class SecondFixtureSettings
{
    [EmailAddress]
    public string? Email { get; set; }

    public bool Enabled { get; set; }
}

public sealed class NestedFixture
{
    public string? Label { get; set; }

    [StringLength(10, MinimumLength = 2)]
    public string Code { get; set; } = "";
}

[ConfiglueModel("fixture.secret")]
public partial class SecretFixtureSettings
{
    public string Host { get; set; } = "localhost";

    [SecretValue]
    public string ApiKey { get; set; } = "";

    [SecretValue]
    public NestedSecretFixture Credentials { get; set; } = new();

    [SecretValue]
    public List<string> Tokens { get; set; } = [];
}

public sealed class NestedSecretFixture
{
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";
}
