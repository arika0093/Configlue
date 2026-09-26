using Configlue;

namespace Example.WorkerService;

[ConfiglueModel(1, Id = "example.worker-settings")]
public partial class SampleSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }
}
