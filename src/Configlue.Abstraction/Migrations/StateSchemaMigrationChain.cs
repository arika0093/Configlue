namespace Configlue;

/// <summary>Applies a validated sequence of migrations to one target schema.</summary>
/// <typeparam name="T">The state or source-contract type being migrated.</typeparam>
public sealed class StateSchemaMigrationChain<T>
{
    private readonly Dictionary<StateSchemaMetadata, IStateSchemaMigration<T>> _migrations;

    /// <summary>Creates a migration chain that ends at <paramref name="targetSchema"/>.</summary>
    public StateSchemaMigrationChain(
        StateSchemaMetadata targetSchema,
        IEnumerable<IStateSchemaMigration<T>>? migrations = null)
    {
        if (targetSchema.Version < StateSchemaMetadata.InitialVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSchema));
        }

        TargetSchema = targetSchema;
        _migrations = new Dictionary<StateSchemaMetadata, IStateSchemaMigration<T>>();
        foreach (var migration in migrations ?? [])
        {
            if (migration is null)
            {
                throw new ArgumentException("Schema migrations cannot contain null values.", nameof(migrations));
            }

            if (migration.SourceSchema.Version < StateSchemaMetadata.InitialVersion ||
                migration.TargetSchema.Version < migration.SourceSchema.Version ||
                migration.TargetSchema == migration.SourceSchema)
            {
                throw new ArgumentException("Schema migrations must advance to a distinct, non-older schema.", nameof(migrations));
            }

            if (!_migrations.TryAdd(migration.SourceSchema, migration))
            {
                throw new ArgumentException($"More than one schema migration starts at '{migration.SourceSchema}'.", nameof(migrations));
            }
        }
    }

    /// <summary>The schema produced by this chain.</summary>
    public StateSchemaMetadata TargetSchema { get; }

    /// <summary>Migrates a value from <paramref name="sourceSchema"/> to the configured target schema.</summary>
    public async ValueTask<T> MigrateAsync(
        T value,
        StateSchemaMetadata sourceSchema,
        CancellationToken cancellationToken = default)
    {
        if (sourceSchema.Version < StateSchemaMetadata.InitialVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSchema));
        }

        if (sourceSchema == TargetSchema)
        {
            return value;
        }

        var currentSchema = sourceSchema;
        var currentValue = value;
        var visited = new HashSet<StateSchemaMetadata>();
        while (currentSchema != TargetSchema)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(currentSchema))
            {
                throw new InvalidOperationException($"Schema migration cycle detected at '{currentSchema}'.");
            }

            if (!_migrations.TryGetValue(currentSchema, out var migration) ||
                migration.TargetSchema.Version > TargetSchema.Version)
            {
                throw new InvalidOperationException(
                    $"No schema migration path exists from '{sourceSchema}' to '{TargetSchema}'.");
            }

            currentValue = await migration.MigrateAsync(currentValue, cancellationToken).ConfigureAwait(false);
            if (currentValue is null)
            {
                throw new InvalidOperationException($"Schema migration from '{migration.SourceSchema}' returned a null value.");
            }

            currentSchema = migration.TargetSchema;
        }

        return currentValue;
    }
}
