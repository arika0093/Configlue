using Configlue;

namespace PackageBasic;

[ConfiglueModel("SampleSetting", Version = 1)]
public partial class SampleSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }
}
