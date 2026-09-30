namespace Configlue.Source.PostgreSql;

/// <summary>Reports that the PostgreSQL database is missing required schema objects or versions.</summary>
public sealed class PostgreSqlSchemaException : InvalidOperationException
{
    /// <summary>Creates a schema error with a clear remediation message.</summary>
    public PostgreSqlSchemaException(string message)
        : base(message) { }

    /// <summary>Creates a schema error with a clear remediation message and inner exception.</summary>
    public PostgreSqlSchemaException(string message, Exception innerException)
        : base(message, innerException) { }
}
