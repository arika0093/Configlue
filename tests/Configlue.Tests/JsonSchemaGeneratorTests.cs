using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Configlue.Provider.Json;

namespace Configlue.Tests;

[ConfiglueModel(2, Id = "schema-settings")]
public partial class SchemaSettings
{
    [Range(1, 1000)]
    public int MaxConnections { get; set; }

    [Required]
    [MinLength(3)]
    public string Name { get; set; } = "";

    [Required]
    [EmailAddress]
    public string? Email { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Published date", Description = "Date shown to users.")]
    public string PublishedDate { get; set; } = "";

    [AllowedValues("red", "green")]
    public string AllowedState { get; set; } = "";

    [DeniedValues("retired", "legacy")]
    public string CurrentState { get; set; } = "";
}

[ConfiglueModel(1, Id = "schema-one-of")]
public partial class OneOfSchemaSettings
{
    [JsonConverter(typeof(CoalescedValueJsonConverter))]
    [JsonSchemaOneOf(typeof(string), typeof(decimal))]
    public string FlexibleValue { get; set; } = "";
}

[ConfiglueModel(1, Id = "schema-invalid-override")]
public partial class InvalidOverrideSettings
{
    [JsonConverter(typeof(CoalescedValueJsonConverter))]
    [JsonSchemaOverride("[]")]
    public string Value { get; set; } = "";
}

public sealed class CoalescedValueJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new JsonException("Expected a string or number."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

[JsonSerializable(typeof(SchemaSettings))]
[JsonSerializable(typeof(OneOfSchemaSettings))]
[JsonSerializable(typeof(InvalidOverrideSettings))]
internal partial class SchemaJsonContext : JsonSerializerContext { }

public sealed class JsonSchemaGeneratorTests
{
    [Test]
    public async Task Generate_UsesConfiglueIdentityAndMapsValidationAttributes()
    {
        var result = JsonSchemaGenerator.Generate<SchemaSettings, SchemaSettings.Fragment>(
            SchemaJsonContext.Default,
            "https://example.test/schemas/");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Documents.Count).IsEqualTo(1);
        var document = result.Documents[0];
        await Assert.That(document.FileName).IsEqualTo("schema-settings.v2.json");
        await Assert.That(document.Schema["$id"]!.GetValue<string>()).IsEqualTo(document.FileName);

        var properties = document.Schema["properties"]!;
        await Assert.That(properties["$version"]!["type"]!.GetValue<string>()).IsEqualTo("integer");
        await Assert.That(properties["$schema"]!["type"]!.GetValue<string>()).IsEqualTo("string");
        await Assert.That(properties["MaxConnections"]!["minimum"]!.GetValue<decimal>()).IsEqualTo(1m);
        await Assert.That(properties["MaxConnections"]!["maximum"]!.GetValue<decimal>()).IsEqualTo(1000m);
        await Assert.That(properties["Name"]!["minLength"]!.GetValue<int>()).IsEqualTo(3);
        await Assert.That(properties["Email"]!["format"]!.GetValue<string>()).IsEqualTo("email");
        await Assert.That(properties["PublishedDate"]!["format"]!.GetValue<string>()).IsEqualTo("date");
        await Assert.That(properties["PublishedDate"]!["title"]!.GetValue<string>()).IsEqualTo("Published date");
        await Assert.That(properties["PublishedDate"]!["description"]!.GetValue<string>())
            .IsEqualTo("Date shown to users.");
        await Assert.That(properties["AllowedState"]!["enum"]!.AsArray()
            .Select(static item => item!.GetValue<string>())).IsEquivalentTo(["red", "green"]);
        await Assert.That(properties["CurrentState"]!["not"]!["enum"]!.AsArray()
            .Select(static item => item!.GetValue<string>())).IsEquivalentTo(["retired", "legacy"]);
        await Assert.That(document.Schema["required"]!.AsArray()
            .Select(static item => item!.GetValue<string>())).IsEquivalentTo(["Name", "Email"]);
    }

    [Test]
    public async Task Generate_UsesOneOfForCustomConverterSchema()
    {
        var result = JsonSchemaGenerator.Generate<OneOfSchemaSettings, OneOfSchemaSettings.Fragment>(
            SchemaJsonContext.Default);

        await Assert.That(result.Succeeded).IsTrue();
        var alternatives = result.Documents[0].Schema["properties"]!["FlexibleValue"]!["oneOf"]!.AsArray();
        await Assert.That(alternatives[0]!["type"]!.AsArray()
            .Select(static item => item!.GetValue<string>())).IsEquivalentTo(["string", "null"]);
        await Assert.That(alternatives[1]!["type"]!.GetValue<string>()).IsEqualTo("number");
    }

    [Test]
    public async Task Generate_DiagnosesInvalidCustomConverterSchemaOverride()
    {
        var result = JsonSchemaGenerator.Generate<InvalidOverrideSettings, InvalidOverrideSettings.Fragment>(
            SchemaJsonContext.Default);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Documents).IsEmpty();
        await Assert.That(result.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("CWSC004");
    }

    [Test]
    public async Task Generate_ReportsMissingResolverMetadata()
    {
        var result = JsonSchemaGenerator.Generate(
            [SchemaSettings.ConfiglueSchema],
            new MissingTypeInfoResolver());

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Documents).IsEmpty();
        await Assert.That(result.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("CWSC002");
        await Assert.That(result.Diagnostics[0].ModelId).IsEqualTo("schema-settings");
    }

    [Test]
    public async Task Generate_ReportsModelsThatWouldWriteTheSameFile()
    {
        var models = new[]
        {
            new ConfiglueModelSchema(typeof(SchemaSettings), "duplicate", 1, []),
            new ConfiglueModelSchema(typeof(string), "duplicate", 1, []),
        };

        var result = JsonSchemaGenerator.Generate(models, SchemaJsonContext.Default);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Documents).IsEmpty();
        await Assert.That(result.Diagnostics.Count).IsEqualTo(2);
        await Assert.That(result.Diagnostics.All(static diagnostic => diagnostic.Code == "CWSC011")).IsTrue();
    }

    [Test]
    public async Task Generate_RejectsModelIdsThatCannotBeFileNames()
    {
        var model = new ConfiglueModelSchema(typeof(SchemaSettings), "../outside", 1, []);
        var result = JsonSchemaGenerator.Generate([model], SchemaJsonContext.Default);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("CWSC009");
    }

    [Test]
    public async Task Write_CreatesVersionedSchemaFile()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"configlue-schema-{Guid.NewGuid():N}");
        try
        {
            var result = JsonSchemaGenerator.Write<SchemaSettings, SchemaSettings.Fragment>(
                outputDirectory,
                SchemaJsonContext.Default);

            await Assert.That(result.Succeeded).IsTrue();
            await Assert.That(result.WrittenFiles.Count).IsEqualTo(1);
            var schema = JsonNode.Parse(await File.ReadAllTextAsync(result.WrittenFiles[0]));
            await Assert.That(schema!["$id"]!.GetValue<string>()).IsEqualTo("schema-settings.v2.json");
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task Write_ReturnsDiagnosticForInvalidOutputDirectory()
    {
        var result = JsonSchemaGenerator.Write(
            [SchemaSettings.ConfiglueSchema],
            "invalid\0directory",
            SchemaJsonContext.Default);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("CWSC005");
        await Assert.That(result.WrittenFiles).IsEmpty();
    }

    private sealed class MissingTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
    }
}
