using Configlue;

namespace Example.ConsoleApp.Yaml;

[ConfiglueModel(1, Id = "example.yaml-settings")]
public partial class SampleSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }
}
