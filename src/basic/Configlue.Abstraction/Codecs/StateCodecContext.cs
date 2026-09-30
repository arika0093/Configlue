namespace Configlue.Codecs;

/// <summary>Options and metadata supplied to a codec operation.</summary>
public readonly struct StateCodecContext
{
    /// <summary>Creates a codec context.</summary>
    public StateCodecContext(StateSchemaMetadata? schema = null, IServiceProvider? services = null)
    {
        Schema = schema;
        Services = services;
        SchemaReferenceBaseUri = null;
    }

    /// <summary>Creates a codec context with a base URI for a versioned instance schema reference.</summary>
    public StateCodecContext(
        StateSchemaMetadata? schema,
        IServiceProvider? services,
        string? schemaReferenceBaseUri
    )
    {
        Schema = schema;
        Services = services;
        SchemaReferenceBaseUri = schemaReferenceBaseUri;
    }

    /// <summary>The schema version associated with the payload.</summary>
    public StateSchemaMetadata? Schema { get; }

    /// <summary>Services available to codecs that need application-specific converters.</summary>
    public IServiceProvider? Services { get; }

    /// <summary>The optional base URI used by JSON or YAML codecs to emit an instance schema reference.</summary>
    public string? SchemaReferenceBaseUri { get; }
}
