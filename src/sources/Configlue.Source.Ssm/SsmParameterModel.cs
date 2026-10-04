namespace Configlue.Source.Ssm;

/// <summary>The Parameter Store parameter type used for writes.</summary>
public enum SsmParameterType
{
    /// <summary>A plain string parameter.</summary>
    String = 0,

    /// <summary>A comma-separated string list parameter.</summary>
    StringList = 1,

    /// <summary>An encrypted secure string parameter (requires a KMS key for writes).</summary>
    SecureString = 2,
}

/// <summary>The Parameter Store tier used for writes.</summary>
public enum SsmParameterTier
{
    /// <summary>The standard tier.</summary>
    Standard = 0,

    /// <summary>The advanced tier.</summary>
    Advanced = 1,

    /// <summary>The intelligent-tiering tier.</summary>
    IntelligentTiering = 2,
}

/// <summary>Safe, value-free provenance for one Parameter Store parameter.</summary>
/// <remarks>Never carries parameter values, decrypted payloads, credentials, or KMS material.</remarks>
public sealed record SsmParameterProvenance
{
    /// <summary>The full parameter name (for example <c>/myapp/prod/database/host</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The parameter version reported by Parameter Store.</summary>
    public required long Version { get; init; }

    /// <summary>The parameter type name (<c>String</c>, <c>StringList</c>, or <c>SecureString</c>).</summary>
    public required string Type { get; init; }

    /// <summary>The parameter tier name, when reported.</summary>
    public string? Tier { get; init; }

    /// <summary>The parameter ARN, when reported.</summary>
    public string? Arn { get; init; }

    /// <summary>The parameter data type (for example <c>text</c>), when reported.</summary>
    public string? DataType { get; init; }
}
