namespace Configlue.Codecs;

/// <summary>Options and metadata supplied to a codec operation.</summary>
public readonly struct StateCodecContext
{
    /// <summary>Creates a codec context.</summary>
    public StateCodecContext(StateSchemaMetadata? schema = null, IServiceProvider? services = null)
    {
        Schema = ValidateSchema(schema);
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
        Schema = ValidateSchema(schema);
        Services = services;
        SchemaReferenceBaseUri = schemaReferenceBaseUri;
    }

    /// <summary>The schema version associated with the payload.</summary>
    public StateSchemaMetadata? Schema { get; }

    /// <summary>Services available to codecs that need application-specific converters.</summary>
    public IServiceProvider? Services { get; }

    /// <summary>The optional base URI used by JSON or YAML codecs to emit an instance schema reference.</summary>
    public string? SchemaReferenceBaseUri { get; }

    private static StateSchemaMetadata? ValidateSchema(StateSchemaMetadata? schema)
    {
        if (schema is { IsValid: false })
        {
            throw new ArgumentException(
                "Codec schema metadata must have a positive schema version.",
                nameof(schema)
            );
        }
        return schema;
    }
}
