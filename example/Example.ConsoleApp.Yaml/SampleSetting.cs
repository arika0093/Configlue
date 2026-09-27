using Configlue;

namespace Example.ConsoleApp.Yaml;

[ConfiglueModel("example.yaml-settings", Version = 1)]
public partial class SampleSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }
}
