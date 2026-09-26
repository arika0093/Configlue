using Configlue;

namespace Example.SimpleApp;

// The generator creates the persisted Fragment type from this application model.
[ConfiglueModel(1, Id = "example.simple-settings")]
public partial class SampleSetting
{
    public string Name { get; set; } = "World";

    public int RunCount { get; set; }
}
