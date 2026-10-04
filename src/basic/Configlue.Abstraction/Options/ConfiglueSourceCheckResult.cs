namespace Configlue;

/// <summary>Reports the outcome of checking one state source during a check operation.</summary>
/// <remarks>Advanced application API for operational checks.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class ConfiglueSourceCheckResult
{
    /// <summary>Creates a source check result.</summary>
    /// <param name="source">The identity and resolved placement of the checked source.</param>
    /// <param name="status">The check outcome for the source.</param>
    /// <param name="contributed">Whether the source contributed to the resolved state.</param>
    /// <param name="fallbackContinued">Whether resolution continued to lower-priority sources after this source.</param>
    /// <param name="validationIssues">Source-local validation or operational issues, when any.</param>
    /// <param name="exception">The exception that faulted this source, when any.</param>
    public ConfiglueSourceCheckResult(
        ConfigSourceDetails source,
        ConfiglueCheckStatus status,
        bool contributed,
        bool fallbackContinued,
        IReadOnlyList<string>? validationIssues = null,
        Exception? exception = null
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(typeof(ConfiglueCheckStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Source = source;
        Status = status;
        Contributed = contributed;
        FallbackContinued = fallbackContinued;
        ValidationIssues = validationIssues ?? Array.Empty<string>();
        Exception = exception;
    }

    /// <summary>The identity, display metadata, and resolved placement of the checked source.</summary>
    public ConfigSourceDetails Source { get; }

    /// <summary>The check outcome for this source.</summary>
    public ConfiglueCheckStatus Status { get; }

    /// <summary>Whether this source contributed a value to the resolved state.</summary>
    public bool Contributed { get; }

    /// <summary>
    /// Whether resolution continued to lower-priority sources after this source returned a non-success status.
    /// Always false for a source that contributed or that terminated resolution.
    /// </summary>
    public bool FallbackContinued { get; }

    /// <summary>Source-local validation or operational issues reported for this source.</summary>
    public IReadOnlyList<string> ValidationIssues { get; }

    /// <summary>The exception that faulted this source, or null when the source did not fault.</summary>
    public Exception? Exception { get; }

    /// <summary>Whether this source contributed to the resolved state.</summary>
    public bool IsSuccess => Status == ConfiglueCheckStatus.Success;
}
