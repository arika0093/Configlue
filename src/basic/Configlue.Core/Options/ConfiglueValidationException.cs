namespace Configlue;

/// <summary>Reports validation failures for a Configlue state value.</summary>
public sealed class ConfiglueValidationException : Exception
{
    /// <summary>Creates an exception containing the validation failures for a state value.</summary>
    public ConfiglueValidationException(
        string stateName,
        Type stateType,
        IEnumerable<string> failures
    )
        : this(
            stateName,
            stateType,
            failures?.ToArray() ?? throw new ArgumentNullException(nameof(failures))
        ) { }

    private ConfiglueValidationException(string stateName, Type stateType, string[] failures)
        : base(
            $"State validation failed for '{stateName}' ({stateType}): {string.Join("; ", failures)}"
        )
    {
        ArgumentNullException.ThrowIfNull(stateName);
        ArgumentNullException.ThrowIfNull(stateType);
        if (failures.Length == 0)
        {
            throw new ArgumentException(
                "At least one validation failure is required.",
                nameof(failures)
            );
        }

        StateName = stateName;
        StateType = stateType;
        Failures = Array.AsReadOnly(failures);
    }

    /// <summary>The name of the state instance that failed validation.</summary>
    public string StateName { get; }

    /// <summary>The type of the state value that failed validation.</summary>
    public Type StateType { get; }

    /// <summary>The validation error messages.</summary>
    public IReadOnlyList<string> Failures { get; }
}
