using System.Text.Json.Serialization;
using SparseFragments;

namespace SparseFragments.JsonPatch.Tests;

// Mirrors ConfigluePatchWidget below.
[SparseFragmentModel]
public partial class PatchWidget
{
    public string? Name { get; set; }

    public int Count { get; set; }

    public bool Enabled { get; set; } = true;

    public PatchNested? Nested { get; set; }

    public List<string> Tags { get; set; } = new();
}

[SparseFragmentModel]
public partial class PatchNested
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; }
}

// Mirrors ConfigluePatchNaming below: explicit wire names requiring escaping.
[SparseFragmentModel]
public partial class PatchNaming
{
    [JsonPropertyName("customName")]
    public string? Value { get; set; }

    [JsonPropertyName("a/b")]
    public int Slash { get; set; }

    [JsonPropertyName("m~n")]
    public int Tilde { get; set; }

    public int Plain { get; set; }
}

// Member names colliding with the bridge surface use the Sparse prefix.
[SparseFragmentModel]
public partial class PatchCollision
{
    public string? FromJsonPatch { get; set; }

    public string? ToJsonPatch { get; set; }

    public int Count { get; set; }
}

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
