using System.Text.Json.Serialization;
using Configlue;

namespace Example.ConsoleApp.NativeAot;

[ConfiglueModel("example.native-aot-settings", Version = 1)]
public partial class SampleSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SampleSetting))]
internal partial class SampleSettingJsonContext : JsonSerializerContext { }
