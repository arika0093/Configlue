namespace Configlue.Source.Presets;

internal sealed record CommonFileSourceSettings
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public int Priority { get; init; }
    public string? SectionPath { get; init; }
    public string? SchemaReferenceBaseUri { get; init; }
    public bool ReadOnly { get; init; }
    public bool ExplicitOnly { get; init; }
    public bool WatchChanges { get; init; }
    public System.Text.Json.JsonSerializerOptions? SerializerOptions { get; init; }
    public System.Text.Json.JsonNamingPolicy? PropertyNamingPolicy { get; init; }
    public Configlue.Resources.FileResourceOptions? ResourceOptions { get; init; }
    public IReadOnlyList<Configlue.Transformers.IStateByteTransformer> Transformers { get; init; } =
    [];
}
