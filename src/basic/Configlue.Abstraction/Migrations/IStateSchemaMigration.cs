namespace Configlue.Migrations;

/// <summary>Migrates one persisted state schema revision to another.</summary>
/// <remarks>Advanced provider SPI for schema evolution.</remarks>
/// <typeparam name="T">The state value type.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IStateSchemaMigration<T>
{
    /// <summary>The schema carried by the input state.</summary>
    StateSchemaMetadata SourceSchema { get; }

    /// <summary>The schema produced by this migration.</summary>
    StateSchemaMetadata TargetSchema { get; }

    /// <summary>Transforms the state to <see cref="TargetSchema"/>.</summary>
    ValueTask<T> MigrateAsync(T value, CancellationToken cancellationToken = default);
}
