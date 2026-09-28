using System.Text.Json.Serialization;
using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

[ConfiglueModel("json-aot-settings", Version = 1)]
public partial class JsonAotSettings
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 443;
}

[JsonSerializable(typeof(JsonAotSettings))]
internal partial class JsonAotSettingsJsonContext : JsonSerializerContext { }

public sealed class JsonAotTests
{
    [Test]
    public async Task JsonAot_PipelineReadUsesGeneratedMetadata()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """{"Host":"stream.example","Port":8443}""");
            using var resource = new FileResource(path);
            var reader = new SerializedStateReader<JsonAotSettings>(
                resource,
                new JsonStateCodec<JsonAotSettings>(
                    JsonAotSettingsJsonContext.Default.JsonAotSettings
                )
                {
                    UseAsyncStreamDecoding = true,
                }
            );

            var result = await reader.ReadAsync();

            result.Status.ShouldBe(StateReadStatus.Success);
            result.Value!.Host.ShouldBe("stream.example");
            result.Value.Port.ShouldBe(8443);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task JsonAot_UsesGeneratedMetadataForProjectedModelState()
    {
        var resource = new InMemoryResource();
        var source = SerializedStateSource.FromResource<JsonAotSettings>(
            "json",
            resource,
            new JsonStateCodec<JsonAotSettings>(JsonAotSettingsJsonContext.Default.JsonAotSettings)
        );
        var projected = StateSourceProjection.Project(
            source,
            static settings => JsonAotSettings.Fragment.From(settings),
            static fragment => fragment.ToModel(),
            projectedSchema: JsonAotSettings.ConfiglueSchema.ToMetadata()
        );
        var options = new ConfiglueOptions<JsonAotSettings, JsonAotSettings.Fragment>(
            new StateSourceSet<JsonAotSettings.Fragment>([projected])
        );

        await options.SaveAsync(patch =>
        {
            patch.Host = "native.example";
            patch.Port = 8443;
        });
        var roundTrip = await options.ReadAsync();

        roundTrip.Value.ShouldNotBeNull();
        roundTrip.Value.Host.ShouldBe("native.example");
        roundTrip.Value.Port.ShouldBe(8443);
    }
}
