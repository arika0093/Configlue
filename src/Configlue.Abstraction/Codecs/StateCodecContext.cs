namespace Configlue;

/// <summary>Options and metadata supplied to a codec operation.</summary>
public readonly struct StateCodecContext
{
    /// <summary>Creates a codec context.</summary>
    public StateCodecContext(StateSchemaMetadata? schema = null, IServiceProvider? services = null)
    {
        Schema = schema;
        Services = services;
    }

    /// <summary>The schema version associated with the payload.</summary>
    public StateSchemaMetadata? Schema { get; }

    /// <summary>Services available to codecs that need application-specific converters.</summary>
    public IServiceProvider? Services { get; }
}
