using System.Buffers;

namespace Configlue.Migrations;

/// <summary>Decodes historical state fragments and converts them to one current state type.</summary>
/// <typeparam name="T">The current state value type.</typeparam>
public sealed class StateSchemaDispatcher<T>
{
    private readonly Dictionary<StateSchemaMetadata, IStateSchemaDispatchEntry<T>> _entries = [];

    /// <summary>Creates a dispatcher for the current schema.</summary>
    public StateSchemaDispatcher(StateSchemaMetadata targetSchema)
    {
        if (!targetSchema.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSchema));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetSchema.ModelId);
        TargetSchema = targetSchema;
    }

    /// <summary>The current schema produced by this dispatcher.</summary>
    public StateSchemaMetadata TargetSchema { get; }

    /// <summary>Registers a typed historical decoder and migration function.</summary>
    /// <typeparam name="THistorical">The historical fragment type.</typeparam>
    /// <param name="sourceSchema">The schema metadata selecting this decoder.</param>
    /// <param name="codec">The codec for the historical fragment type.</param>
    /// <param name="migrate">Converts the decoded historical fragment to the current type.</param>
    /// <returns>This dispatcher.</returns>
    public StateSchemaDispatcher<T> Add<THistorical>(
        StateSchemaMetadata sourceSchema,
        IStateCodec<THistorical> codec,
        Func<THistorical, T> migrate
    )
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(migrate);
        if (
            !sourceSchema.IsValid
            || sourceSchema.Version >= TargetSchema.Version
            || !string.Equals(sourceSchema.ModelId, TargetSchema.ModelId, StringComparison.Ordinal)
        )
        {
            throw new ArgumentException(
                "A historical schema must use the target model ID and an earlier positive version.",
                nameof(sourceSchema)
            );
        }

        if (!_entries.TryAdd(sourceSchema, new Entry<THistorical>(codec, migrate)))
        {
            throw new ArgumentException(
                $"A historical decoder for '{sourceSchema}' is already registered.",
                nameof(sourceSchema)
            );
        }

        return this;
    }

    /// <summary>
    /// Attempts to decode and migrate the payload when its schema is registered with this dispatcher.
    /// </summary>
    /// <param name="sourceSchema">The schema metadata associated with the payload.</param>
    /// <param name="source">The serialized payload.</param>
    /// <param name="services">Services passed to the historical codec.</param>
    /// <param name="value">The migrated value when a historical schema was handled.</param>
    /// <returns>
    /// <see langword="true"/> when a registered historical schema was decoded; otherwise,
    /// <see langword="false"/> when the payload already uses the target schema or another model ID.
    /// </returns>
    /// <remarks>When <paramref name="sourceSchema"/> has no model ID, the dispatcher associates it with <see cref="TargetSchema"/>.</remarks>
    /// <exception cref="InvalidOperationException">
    /// The payload uses an unregistered revision for this model or a newer revision.
    /// </exception>
    public bool TryDeserialize(
        StateSchemaMetadata sourceSchema,
        in ReadOnlySequence<byte> source,
        IServiceProvider? services,
        out T? value
    )
    {
        value = default;
        if (!sourceSchema.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSchema));
        }
        if (sourceSchema.ModelId is null)
        {
            sourceSchema = sourceSchema with { ModelId = TargetSchema.ModelId };
        }

        if (!string.Equals(sourceSchema.ModelId, TargetSchema.ModelId, StringComparison.Ordinal))
        {
            return false;
        }

        if (sourceSchema == TargetSchema)
        {
            return false;
        }

        if (sourceSchema.Version > TargetSchema.Version)
        {
            throw new InvalidOperationException(
                $"State schema '{sourceSchema}' is newer than the configured target '{TargetSchema}'."
            );
        }

        if (!_entries.TryGetValue(sourceSchema, out var entry))
        {
            throw new InvalidOperationException(
                $"No historical fragment decoder is registered for schema '{sourceSchema}'."
            );
        }

        var context = new StateCodecContext(sourceSchema, services);
        value = entry.Deserialize(in source, in context);
        return true;
    }

    private interface IStateSchemaDispatchEntry<out TValue>
    {
        TValue Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context);
    }

    private sealed class Entry<THistorical>(
        IStateCodec<THistorical> codec,
        Func<THistorical, T> migrate
    ) : IStateSchemaDispatchEntry<T>
    {
        public T Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context)
        {
            var historical = codec.Deserialize(in source, in context);
            if (historical is null)
            {
                throw new InvalidOperationException(
                    "A historical state codec returned a null fragment."
                );
            }

            return migrate(historical)
                ?? throw new InvalidOperationException(
                    "A historical fragment migration returned a null value."
                );
        }
    }
}
