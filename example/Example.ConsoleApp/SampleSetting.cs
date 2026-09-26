using Configlue;

namespace Example.ConsoleApp;

[ConfiglueModel(1, Id = "example.console-settings")]
public partial class SampleSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }
}
