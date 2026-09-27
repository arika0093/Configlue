using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Configlue.Provider.Json;

namespace Configlue.Tests;

[ConfiglueModel("schema-settings", Version = 2)]
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

[ConfiglueModel("schema-one-of", Version = 1)]
public partial class OneOfSchemaSettings
{
    [JsonConverter(typeof(CoalescedValueJsonConverter))]
    [JsonSchemaOneOf(typeof(string), typeof(decimal))]
    public string FlexibleValue { get; set; } = "";
}

[ConfiglueModel("schema-invalid-override", Version = 1)]
public partial class InvalidOverrideSettings
{
    [JsonConverter(typeof(CoalescedValueJsonConverter))]
    [JsonSchemaOverride("[]")]
    public string Value { get; set; } = "";
}

public sealed class CoalescedValueJsonConverter : JsonConverter<string>
{
    public override string? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader
                .GetDecimal()
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new JsonException("Expected a string or number."),
        };

    public override void Write(
        Utf8JsonWriter writer,
        string value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(value);
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
            "https://example.test/schemas/"
        );

        (result.Succeeded).ShouldBeTrue();
        (result.Documents.Count).ShouldBe(1);
        var document = result.Documents[0];
        (document.FileName).ShouldBe("schema-settings.v2.json");
        (document.Schema["$id"]!.GetValue<string>()).ShouldBe(
            "https://example.test/schemas/schema-settings.v2.json"
        );

        // The default simple layout stores the version inline with the payload.
        var properties = document.Schema["properties"]!;
        (properties["$schema"]!["type"]!.GetValue<string>()).ShouldBe("string");
        (properties["$version"]!["const"]!.GetValue<int>()).ShouldBe(2);
        (properties["$configlue"]).ShouldBeNull();
        (properties["$value"]).ShouldBeNull();
        (properties["MaxConnections"]!["minimum"]!.GetValue<decimal>()).ShouldBe(1m);
        (properties["MaxConnections"]!["maximum"]!.GetValue<decimal>()).ShouldBe(1000m);
        (properties["Name"]!["minLength"]!.GetValue<int>()).ShouldBe(3);
        (properties["Email"]!["format"]!.GetValue<string>()).ShouldBe("email");
        (properties["PublishedDate"]!["format"]!.GetValue<string>()).ShouldBe("date");
        (properties["PublishedDate"]!["title"]!.GetValue<string>()).ShouldBe("Published date");
        (properties["PublishedDate"]!["description"]!.GetValue<string>()).ShouldBe(
            "Date shown to users."
        );
        (
            (
                properties["AllowedState"]!["enum"]!
                    .AsArray()
                    .Select(static item => item!.GetValue<string>())
            )
        )
            .OrderBy(static item => item)
            .ShouldBe((new[] { "red", "green" }).OrderBy(static item => item));
        (
            (
                properties["CurrentState"]!["not"]!["enum"]!
                    .AsArray()
                    .Select(static item => item!.GetValue<string>())
            )
        )
            .OrderBy(static item => item)
            .ShouldBe((new[] { "retired", "legacy" }).OrderBy(static item => item));
        (
            (document.Schema["required"]!.AsArray().Select(static item => item!.GetValue<string>()))
        ).ShouldBe(["$version"]);
        (properties["required"]).ShouldBeNull();
    }

    [Test]
    public async Task Generate_SupportsDetailedLayout()
    {
        var result = JsonSchemaGenerator.Generate<SchemaSettings, SchemaSettings.Fragment>(
            SchemaJsonContext.Default,
            "https://example.test/schemas/",
            new DocumentLayoutOptions { Layout = DocumentLayout.Detailed }
        );

        (result.Succeeded).ShouldBeTrue();
        var document = result.Documents[0];
        var properties = document.Schema["properties"]!;
        var metadataProperties = properties["$configlue"]!["properties"]!;
        (metadataProperties["id"]!["const"]!.GetValue<string>()).ShouldBe("schema-settings");
        (metadataProperties["version"]!["const"]!.GetValue<int>()).ShouldBe(2);
        var valueSchema = properties["$value"]!;
        (valueSchema["properties"]!["MaxConnections"]!["minimum"]!.GetValue<decimal>()).ShouldBe(
            1m
        );
        ((document.Schema["required"]!.AsArray().Select(static item => item!.GetValue<string>())))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "$configlue", "$value" }).OrderBy(static item => item));
        (valueSchema["required"]).ShouldBeNull();
    }

    [Test]
    public async Task Generate_UsesOneOfForCustomConverterSchema()
    {
        var result = JsonSchemaGenerator.Generate<
            OneOfSchemaSettings,
            OneOfSchemaSettings.Fragment
        >(SchemaJsonContext.Default);

        (result.Succeeded).ShouldBeTrue();
        var alternatives = result.Documents[0].Schema["properties"]!["FlexibleValue"]![
            "oneOf"
        ]!.AsArray();
        ((alternatives[0]!["type"]!.AsArray().Select(static item => item!.GetValue<string>())))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "string", "null" }).OrderBy(static item => item));
        (alternatives[1]!["type"]!.GetValue<string>()).ShouldBe("number");
    }

    [Test]
    public async Task Generate_DiagnosesInvalidCustomConverterSchemaOverride()
    {
        var result = JsonSchemaGenerator.Generate<
            InvalidOverrideSettings,
            InvalidOverrideSettings.Fragment
        >(SchemaJsonContext.Default);

        (result.Succeeded).ShouldBeFalse();
        (result.Documents).ShouldBeEmpty();
        (result.Diagnostics.Count).ShouldBe(1);
        (result.Diagnostics[0].Code).ShouldBe("CWSC004");
    }

    [Test]
    public async Task Generate_ReportsMissingResolverMetadata()
    {
        var result = JsonSchemaGenerator.Generate(
            [SchemaSettings.ConfiglueSchema],
            new MissingTypeInfoResolver()
        );

        (result.Succeeded).ShouldBeFalse();
        (result.Documents).ShouldBeEmpty();
        (result.Diagnostics.Count).ShouldBe(1);
        (result.Diagnostics[0].Code).ShouldBe("CWSC002");
        (result.Diagnostics[0].ModelId).ShouldBe("schema-settings");
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

        (result.Succeeded).ShouldBeFalse();
        (result.Documents).ShouldBeEmpty();
        (result.Diagnostics.Count).ShouldBe(2);
        (result.Diagnostics.All(static diagnostic => diagnostic.Code == "CWSC011")).ShouldBeTrue();
    }

    [Test]
    public async Task Generate_RejectsModelIdsThatCannotBeFileNames()
    {
        var model = new ConfiglueModelSchema(typeof(SchemaSettings), "../outside", 1, []);
        var result = JsonSchemaGenerator.Generate([model], SchemaJsonContext.Default);

        (result.Succeeded).ShouldBeFalse();
        (result.Diagnostics.Count).ShouldBe(1);
        (result.Diagnostics[0].Code).ShouldBe("CWSC009");
    }

    [Test]
    public async Task Write_CreatesVersionedSchemaFile()
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"configlue-schema-{Guid.NewGuid():N}"
        );
        try
        {
            var result = JsonSchemaGenerator.Write<SchemaSettings, SchemaSettings.Fragment>(
                outputDirectory,
                SchemaJsonContext.Default
            );

            (result.Succeeded).ShouldBeTrue();
            (result.WrittenFiles.Count).ShouldBe(1);
            var schema = JsonNode.Parse(await File.ReadAllTextAsync(result.WrittenFiles[0]));
            (schema!["$id"]!.GetValue<string>()).ShouldBe("schema-settings.v2.json");
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
            SchemaJsonContext.Default
        );

        (result.Succeeded).ShouldBeFalse();
        (result.Diagnostics.Count).ShouldBe(1);
        (result.Diagnostics[0].Code).ShouldBe("CWSC005");
        (result.WrittenFiles).ShouldBeEmpty();
    }

    private sealed class MissingTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
    }
}
