namespace Configlue;

/// <summary>Reports the final state-level outcome of one check operation.</summary>
/// <remarks>Advanced application API for operational checks.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class ConfiglueCheckResult
{
    private ConfiglueCheckResult(
        ConfiglueCheckStatus status,
        IReadOnlyList<string> validationIssues,
        Exception? exception
    )
    {
        Status = status;
        ValidationIssues = validationIssues;
        Exception = exception;
    }

    /// <summary>The final resolution outcome.</summary>
    public ConfiglueCheckStatus Status { get; }

    /// <summary>
    /// The effective, resolved-model validation issues that failed the check. Empty for a non-invalid result.
    /// Source-local issues are reported by the source stream instead of being duplicated here.
    /// </summary>
    public IReadOnlyList<string> ValidationIssues { get; }

    /// <summary>The exception that faulted resolution, or null when resolution did not fault.</summary>
    public Exception? Exception { get; }

    /// <summary>Whether the state resolved successfully.</summary>
    public bool IsResolved => Status == ConfiglueCheckStatus.Success;

    /// <summary>Creates a successful resolution result.</summary>
    public static ConfiglueCheckResult Resolved() =>
        new(ConfiglueCheckStatus.Success, Array.Empty<string>(), null);

    /// <summary>Creates a result for a state that resolved to no value.</summary>
    public static ConfiglueCheckResult NotFound() =>
        new(ConfiglueCheckStatus.NotFound, Array.Empty<string>(), null);

    /// <summary>Creates a result for a state that is temporarily unavailable.</summary>
    public static ConfiglueCheckResult Unavailable() =>
        new(ConfiglueCheckStatus.Unavailable, Array.Empty<string>(), null);

    /// <summary>Creates a result for a state that failed effective validation.</summary>
    /// <param name="validationIssues">The effective, resolved-model validation failures.</param>
    public static ConfiglueCheckResult Invalid(IEnumerable<string>? validationIssues = null)
    {
        var issues = validationIssues is null
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : Array.AsReadOnly(validationIssues.ToArray());
        return new ConfiglueCheckResult(ConfiglueCheckStatus.Invalid, issues, null);
    }

    /// <summary>Creates a result for a check that faulted with an exception.</summary>
    public static ConfiglueCheckResult Faulted(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new ConfiglueCheckResult(
            ConfiglueCheckStatus.Faulted,
            Array.Empty<string>(),
            exception
        );
    }
}
