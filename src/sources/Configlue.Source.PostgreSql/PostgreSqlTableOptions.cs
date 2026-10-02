namespace Configlue.Source.PostgreSql;

/// <summary>Configures the table and identity of a PostgreSQL JSONB source.</summary>
public sealed class PostgreSqlTableOptions
{
    /// <summary>The PostgreSQL schema. Defaults to <c>public</c>.</summary>
    public string SchemaName { get; init; } = "public";

    /// <summary>The table holding JSONB state rows. Defaults to <c>configlue_state</c>.</summary>
    public string TableName { get; init; } = "configlue_state";

    /// <summary>The table tracking applied schema component versions. Defaults to <c>configlue_schema_components</c>.</summary>
    public string ComponentsTableName { get; init; } = "configlue_schema_components";

    /// <summary>An optional fixed identity overriding the identity derived from the row and route.</summary>
    public ResourceId? ResourceId { get; init; }

    internal TimeSpan BackendCacheIdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    internal int BackendCacheCapacity { get; init; } = 256;

    internal void Validate()
    {
        ValidateIdentifier(SchemaName, nameof(SchemaName));
        ValidateIdentifier(TableName, nameof(TableName));
        ValidateIdentifier(ComponentsTableName, nameof(ComponentsTableName));
    }

    internal static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (
            !IsIdentifierStart(value[0])
            || value.Any(static character => !IsIdentifierPart(character))
        )
        {
            throw new ArgumentException(
                "PostgreSQL identifiers must start with an ASCII letter or underscore and contain only ASCII letters, digits, and underscores.",
                parameterName
            );
        }

        if (value.Length > 63)
        {
            throw new ArgumentException(
                "PostgreSQL identifiers cannot exceed 63 bytes.",
                parameterName
            );
        }
    }

    internal static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';

    private static bool IsIdentifierStart(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsIdentifierPart(char value) =>
        IsIdentifierStart(value) || value is >= '0' and <= '9';
}
