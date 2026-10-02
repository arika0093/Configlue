using SparseFragments;

[SparseFragmentModel]
public partial class LegacySettings
{
    public LegacySettings() { }

    public int Count { get; init; }
}

public static class LegacyConsumer
{
    public static LegacySettings.Fragment Create() => new() { Count = 7 };
}
