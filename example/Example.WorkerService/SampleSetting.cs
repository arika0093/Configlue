using Configlue;

namespace Example.WorkerService;

[ConfiglueModel("example.worker-settings", Version = 1)]
public partial class SampleSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }
}
