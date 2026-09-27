using Configlue;

namespace Example.MultiSource;

[ConfiglueModel("example.multi-source-settings", Version = 1)]
public partial class SampleSetting
{
    public string Name { get; set; } = "Global default";

    public string Policy { get; set; } = "Global policy";

    public string Region { get; set; } = "Global region";
}
