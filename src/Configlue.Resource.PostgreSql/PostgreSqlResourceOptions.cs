namespace Configlue.Resource.PostgreSql;

/// <summary>Configures the table and identity of a PostgreSQL resource.</summary>
public sealed class PostgreSqlResourceOptions
{
    /// <summary>The PostgreSQL schema. Defaults to <c>public</c>.</summary>
    public string SchemaName { get; init; } = "public";

    /// <summary>The table holding resource rows. Defaults to <c>configlue_state</c>.</summary>
    public string TableName { get; init; } = "configlue_state";

    /// <summary>An optional fixed identity overriding the identity derived from the row and route.</summary>
    public ResourceId? ResourceId { get; init; }

    internal void Validate()
    {
        ValidateIdentifier(SchemaName, nameof(SchemaName));
        ValidateIdentifier(TableName, nameof(TableName));
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

    private static bool IsIdentifierStart(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsIdentifierPart(char value) =>
        IsIdentifierStart(value) || value is >= '0' and <= '9';
}
