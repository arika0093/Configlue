using Configlue;

namespace Configlue.Tests;

[ConfiglueModel("messagepack.sample", Version = 2)]
[ConfigluePreviousVersion(typeof(MessagePackPreviousSampleSettings))]
public partial class MessagePackSampleSettings
{
    public string Name { get; set; } = string.Empty;

    public int Count { get; set; }

    public string? Label { get; set; }

    public List<string> Items { get; set; } = [];

    public MessagePackNestedPoco Nested { get; set; } = new();

    public MessagePackAccentColor Accent { get; set; }
}

public sealed class MessagePackNestedPoco
{
    public bool Enabled { get; set; }

    public string? Text { get; set; }
}

public readonly record struct MessagePackAccentColor(byte R, byte G, byte B);

[ConfiglueModel("messagepack.sample", Version = 1)]
public partial class MessagePackPreviousSampleSettings
{
    public string Name { get; set; } = string.Empty;

    public int Count { get; set; }
}

[ConfiglueModel("messagepack.resolver", Version = 1)]
public partial class MessagePackResolverSettings
{
    public MessagePackOpaqueToken Token { get; set; }
}

public readonly record struct MessagePackOpaqueToken(int Left, int Right);
