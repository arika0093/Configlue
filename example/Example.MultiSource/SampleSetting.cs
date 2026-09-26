using Configlue;

namespace Example.MultiSource;

[ConfiglueModel(1, Id = "example.multi-source-settings")]
public partial class SampleSetting
{
    public string Name { get; set; } = "Global default";

    public string Policy { get; set; } = "Global policy";

    public string Region { get; set; } = "Global region";
}
