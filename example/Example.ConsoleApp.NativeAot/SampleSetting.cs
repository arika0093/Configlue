using System.Text.Json.Serialization;
using Configlue;

namespace Example.ConsoleApp.NativeAot;

[ConfiglueModel(1, Id = "example.native-aot-settings")]
public partial class SampleSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SampleSetting))]
internal partial class SampleSettingJsonContext : JsonSerializerContext { }
