using System.Text.Json.Serialization;
using Configlue;
using SharpYaml.Serialization;

namespace Example.ConsoleApp.NativeAot;

[ConfiglueModel("example.native-aot-settings", Version = 1)]
public partial class SampleSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }

    public DatabaseSettings Database { get; set; } = new();
}

[ConfiglueModel("example.native-aot-database-settings", Version = 1)]
public partial class DatabaseSettings
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SampleSetting))]
internal partial class SampleSettingJsonContext : JsonSerializerContext { }

[YamlSerializable(typeof(SampleSetting))]
[YamlSerializable(typeof(SampleSetting.Fragment))]
[YamlSerializable(typeof(DatabaseSettings.Fragment))]
[YamlSerializable(typeof(Dictionary<string, object?>))]
[YamlSerializable(typeof(int))]
[YamlSerializable(typeof(string))]
internal partial class SampleSettingYamlContext : YamlSerializerContext { }
