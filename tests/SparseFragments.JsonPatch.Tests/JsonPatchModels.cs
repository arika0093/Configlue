using System.Text.Json.Serialization;

namespace SparseFragments.JsonPatch.Tests;

// Configlue-side mirrors of the standalone RFC 6902 interop models that live
// in the SparseFragments repository. These exercise Configlue-generated
// model parity via ConfiglueJsonPatchTests (thin integration suite; the
// canonical behavioral suite in JsonPatchBehavioralTests uses no models).
[Configlue.ConfiglueModel("jsonpatch-widget", Version = 1)]
public partial class ConfigluePatchWidget
{
    public string? Name { get; set; }

    public int Count { get; set; }

    public bool Enabled { get; set; } = true;

    public ConfigluePatchNested? Nested { get; set; }

    public List<string> Tags { get; set; } = new();
}

[Configlue.ConfiglueModel("jsonpatch-nested", Version = 1)]
public partial class ConfigluePatchNested
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; }
}

[Configlue.ConfiglueModel("jsonpatch-naming", Version = 1)]
public partial class ConfigluePatchNaming
{
    [JsonPropertyName("customName")]
    public string? Value { get; set; }

    [JsonPropertyName("a/b")]
    public int Slash { get; set; }

    [JsonPropertyName("m~n")]
    public int Tilde { get; set; }

    public int Plain { get; set; }
}

[Configlue.ConfiglueModel("jsonpatch-collision", Version = 1)]
public partial class ConfigluePatchCollision
{
    public string? Label { get; set; }

    public int Count { get; set; }
}
