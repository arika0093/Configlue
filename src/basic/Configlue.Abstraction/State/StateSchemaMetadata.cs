using System.Text.Json;
using System.Text.Json.Serialization;

namespace Configlue.State;

/// <summary>Identifies the logical schema used to encode a state payload.</summary>
/// <remarks>
/// The zero-initialized value is invalid. Absence is represented by <see langword="null"/>;
/// consumers should reject present values whose <see cref="IsValid"/> property is false.
/// </remarks>
[JsonConverter(typeof(StateSchemaMetadataJsonConverter))]
public readonly record struct StateSchemaMetadata
{
    /// <summary>Gets or initializes the <see cref="ModelId"/> value.</summary>
    public string? ModelId { get; }

    /// <summary>Gets or initializes the <see cref="Version"/> value.</summary>
    public int Version { get; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="ModelId">The initial value for the <see cref="ModelId"/> property.</param>
    /// <param name="Version">The initial value for the <see cref="Version"/> property.</param>
    public StateSchemaMetadata(string? ModelId, int Version)
    {
        if (Version < InitialVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Version),
                "Schema versions must be positive."
            );
        }

        this.ModelId = ModelId;
        this.Version = Version;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="ModelId">Receives the current <see cref="ModelId"/> value.</param>
    /// <param name="Version">Receives the current <see cref="Version"/> value.</param>
    public void Deconstruct(out string? ModelId, out int Version)
    {
        ModelId = this.ModelId;
        Version = this.Version;
    }

    /// <summary>The initial schema version.</summary>
    public const int InitialVersion = 1;

    /// <summary>Whether this value has a valid positive schema version.</summary>
    public bool IsValid => Version >= InitialVersion;

    /// <summary>Whether this value is the uninitialized default struct.</summary>
    public bool IsDefault => !IsValid && ModelId is null;
}

internal sealed class StateSchemaMetadataJsonConverter : JsonConverter<StateSchemaMetadata>
{
    public override StateSchemaMetadata Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var modelId =
            root.TryGetProperty(nameof(StateSchemaMetadata.ModelId), out var modelElement)
            && modelElement.ValueKind != JsonValueKind.Null
                ? modelElement.GetString()
                : null;
        var version = root.TryGetProperty(
            nameof(StateSchemaMetadata.Version),
            out var versionElement
        )
            ? versionElement.GetInt32()
            : 0;
        return version == 0 && modelId is null
            ? default
            : new StateSchemaMetadata(modelId, version);
    }

    public override void Write(
        Utf8JsonWriter writer,
        StateSchemaMetadata value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartObject();
        if (value.ModelId is null)
        {
            writer.WriteNull(nameof(StateSchemaMetadata.ModelId));
        }
        else
        {
            writer.WriteString(nameof(StateSchemaMetadata.ModelId), value.ModelId);
        }
        writer.WriteNumber(nameof(StateSchemaMetadata.Version), value.Version);
        writer.WriteEndObject();
    }
}
