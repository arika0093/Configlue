using Configlue.Source.PostgreSql;

namespace Configlue.Source.PostgreSql.Migrations;

/// <summary>Options for applying PostgreSQL database schema migrations.</summary>
public sealed class PostgreSqlMigrationOptions
{
    /// <summary>The table, schema, and schema-tracking table to create or upgrade.</summary>
    public PostgreSqlTableOptions? TableOptions { get; init; }
}

/// <summary>A component version recorded in the Configlue schema-tracking table.</summary>
/// <param name="Component">The schema component name.</param>
/// <param name="Version">The applied component version.</param>
public readonly record struct PostgreSqlSchemaComponentVersion(string Component, int Version);

/// <summary>The observed Configlue PostgreSQL schema state.</summary>
public sealed class PostgreSqlSchemaStatus
{
    /// <summary>Creates an observed schema status.</summary>
    public PostgreSqlSchemaStatus(
        bool isInitialized,
        IReadOnlyList<PostgreSqlSchemaComponentVersion> components
    )
    {
        IsInitialized = isInitialized;
        Components = components;
    }

    /// <summary>Whether the Configlue schema-tracking table exists.</summary>
    public bool IsInitialized { get; }

    /// <summary>The observed component versions.</summary>
    public IReadOnlyList<PostgreSqlSchemaComponentVersion> Components { get; }
}

/// <summary>The result of applying Configlue PostgreSQL schema migrations.</summary>
public sealed class PostgreSqlSchemaMigrationResult
{
    /// <summary>Creates a migration result.</summary>
    public PostgreSqlSchemaMigrationResult(
        bool changed,
        IReadOnlyList<PostgreSqlSchemaComponentVersion> components
    )
    {
        Changed = changed;
        Components = components;
    }

    /// <summary>Whether any schema object or component version changed.</summary>
    public bool Changed { get; }

    /// <summary>The component versions after migration.</summary>
    public IReadOnlyList<PostgreSqlSchemaComponentVersion> Components { get; }
}
