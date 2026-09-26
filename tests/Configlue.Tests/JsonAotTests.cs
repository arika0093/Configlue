using System.Text.Json.Serialization;
using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

[ConfiglueModel(1, Id = "json-aot-settings")]
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
            static settings => JsonAotSettings.ToFragment(settings),
            static fragment => fragment.ToModel(),
            projectedSchema: JsonAotSettings.ConfiglueSchema.ToMetadata()
        );
        var options = new ConfiglueOptions<JsonAotSettings, JsonAotSettings.Fragment>(
            new StateSourceSet<JsonAotSettings.Fragment>([projected])
        );

        await options.SaveAsync(new JsonAotSettings { Host = "native.example", Port = 8443 });
        var roundTrip = await options.ReadAsync();

        roundTrip.Value.ShouldNotBeNull();
        roundTrip.Value.Host.ShouldBe("native.example");
        roundTrip.Value.Port.ShouldBe(8443);
    }
}
