using System.Text.Json.Serialization;
using Configlue;

namespace NativeAotSupportedConsumer;

[ConfiglueModel("SampleAotSetting", Version = 1)]
public partial class SampleAotSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }

    public List<int> Numbers { get; set; } = [];

    public SampleAotPoco Endpoint { get; set; } = new();

    public List<SampleAotPoco> Endpoints { get; set; } = [];
}

public partial class SampleAotPoco
{
    public string Name { get; set; } = "sample";

    public int Value { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SampleAotSetting))]
internal partial class SampleAotJsonContext : JsonSerializerContext { }
