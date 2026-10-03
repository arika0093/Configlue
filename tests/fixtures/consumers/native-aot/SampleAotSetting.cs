using System.Text.Json.Serialization;
using Configlue;
using MessagePack;

namespace NativeAotConsumer;

[ConfiglueModel("SampleAotSetting", Version = 1)]
public partial class SampleAotSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }

    public List<int> Numbers { get; set; } = [];

    public SampleAotPoco Endpoint { get; set; } = new();

    public List<SampleAotPoco> Endpoints { get; set; } = [];
}

[MessagePackObject]
public partial class SampleAotPoco
{
    [Key(0)]
    public string Name { get; set; } = "sample";

    [Key(1)]
    public int Value { get; set; }
}

[GeneratedMessagePackResolver]
internal partial class SampleMessagePackResolver;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SampleAotSetting))]
internal partial class SampleAotJsonContext : JsonSerializerContext { }
