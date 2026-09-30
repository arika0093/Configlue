namespace Configlue.Source.PostgreSql;

/// <summary>Identifies the Configlue PostgreSQL schema components and their supported versions.</summary>
/// <remarks>
/// Schema versioning is tracked per component so that future optional features can evolve their own
/// database objects without forcing every database to adopt a single global version.
/// </remarks>
public static class PostgreSqlSchemaVersions
{
    /// <summary>The core state table component.</summary>
    public const string CoreComponent = "core";

    /// <summary>The schema version required by the runtime source's core state table.</summary>
    public const int CoreVersion = 1;
}
