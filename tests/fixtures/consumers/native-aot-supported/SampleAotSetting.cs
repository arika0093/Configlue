using System.Text.Json.Serialization;
using Configlue;
using MessagePack;

namespace NativeAotSupportedConsumer;

[ConfiglueModel("SampleAotSetting", Version = 1)]
public partial class SampleAotSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }

    public List<int> Numbers { get; set; } = [];

    public SampleAotPoco Endpoint { get; set; } = new();

    public List<SampleAotPoco> Endpoints { get; set; } = [];

    public string? Label { get; set; }

    public int? RetryLimit { get; set; }

    public SampleAotStatus Status { get; set; } = SampleAotStatus.Unknown;

    public SampleAotChild? Child { get; set; }
}

[ConfiglueModel("SampleAotChild", Version = 1)]
public partial class SampleAotChild
{
    public string Name { get; set; } = "child";

    public int Value { get; set; }
}

public enum SampleAotStatus
{
    Unknown,
    Active,
    Retired,
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
