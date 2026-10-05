using System.Text.Json;

namespace Configlue.Hosting.AspNetCore;

/// <summary>Validated endpoint mapping options shared by the state protocol stages.</summary>
internal sealed record ValidatedStateEndpointOptions(
    bool MapRead,
    bool MapWrite,
    bool MapPatch,
    bool MapEvents,
    string ReadPath,
    string WritePath,
    string EventsPath,
    JsonSerializerOptions? SerializerOptions,
    long? MaximumRequestBodySize
)
{
    public static ValidatedStateEndpointOptions From(ConfiglueStateEndpointOptions options)
    {
        if (!options.MapRead && !options.MapWrite && !options.MapPatch && !options.MapEvents)
        {
            throw new ArgumentException("At least one endpoint must be mapped.", nameof(options));
        }

        StateEndpointRoutes.ValidateStatePath(
            options.ReadPath,
            nameof(options.ReadPath),
            allowEmpty: true
        );
        StateEndpointRoutes.ValidateStatePath(
            options.WritePath,
            nameof(options.WritePath),
            allowEmpty: true
        );
        StateEndpointRoutes.ValidateStatePath(
            options.EventsPath,
            nameof(options.EventsPath),
            allowEmpty: false
        );
        // GET and PUT may share the pattern root (different methods), but the SSE GET
        // endpoint must not collide with the state GET endpoint.
        if (
            options.MapRead
            && options.MapEvents
            && string.Equals(
                StateEndpointRoutes.NormalizeForComparison(options.ReadPath),
                StateEndpointRoutes.NormalizeForComparison(options.EventsPath),
                StringComparison.Ordinal
            )
        )
        {
            throw new ArgumentException(
                "The events endpoint must differ from the read endpoint.",
                nameof(options)
            );
        }

        if (options.MaximumRequestBodySize is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumRequestBodySize must be between one and Int32.MaxValue bytes or null."
            );
        }

        return new ValidatedStateEndpointOptions(
            options.MapRead,
            options.MapWrite,
            options.MapPatch,
            options.MapEvents,
            options.ReadPath,
            options.WritePath,
            options.EventsPath,
            options.SerializerOptions,
            options.MaximumRequestBodySize
        );
    }
}
