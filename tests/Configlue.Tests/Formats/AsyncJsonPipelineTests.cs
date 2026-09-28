using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class AsyncJsonPipelineTests
{
    [Test]
    public async Task StreamsJsoncEnvelopesIntoGeneratedFragmentConverter()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings-envelope.json");
        var document = """
            {
              // The metadata and payload are parsed from the resource stream.
              "$configlue": { "id": "app-settings", "version": 2, },
              "$value": { "RetryCount": 17, "Enabled": false, },
            }
            """;
        var content = Encoding
            .UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(document))
            .ToArray();
        await File.WriteAllBytesAsync(path, content);
        using var resource = new FileResource(path);
        var reader = new SerializedStateReader<AppSettings.Fragment>(
            resource,
            new JsonStateCodec<AppSettings.Fragment> { UseAsyncStreamDecoding = true }
        );

        var result = await reader.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Value!.RetryCount.Value.ShouldBe(17);
        result.Value.Enabled.Value.ShouldBeFalse();
        result.Schema.ShouldBe(new StateSchemaMetadata("app-settings", 2));
    }

    [Test]
    public async Task StripsVersionBeforeStrictJsonDeserialization()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "strict-settings.json");
        await File.WriteAllTextAsync(path, """{"$version":1,"Value":14}""");
        using var resource = new FileResource(path);
        var codec = new JsonStateCodec<StrictJsonPipelineSettings>(
            new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }
        )
        {
            UseAsyncStreamDecoding = true,
        };
        var reader = new SerializedStateReader<StrictJsonPipelineSettings>(resource, codec);

        var result = await reader.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Value!.Value.ShouldBe(14);
        result.Schema.ShouldBe(new StateSchemaMetadata(null, 1));
    }

    [Test]
    public async Task ReadsLargePayloadFromPipeline()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "large-settings.json");
        var payload = new string('x', 2 * 1024 * 1024);
        await File.WriteAllTextAsync(path, "{\"Label\":\"" + payload + "\"}");
        using var resource = new FileResource(path);
        var reader = new SerializedStateReader<AppSettings.Fragment>(
            resource,
            new JsonStateCodec<AppSettings.Fragment> { UseAsyncStreamDecoding = true }
        );

        var result = await reader.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Value!.Label.Value!.Length.ShouldBe(payload.Length);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Configlue.Tests",
                Guid.NewGuid().ToString("N")
            );

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

public sealed class StrictJsonPipelineSettings
{
    public int Value { get; set; }
}
