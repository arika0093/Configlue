namespace Configlue;

/// <summary>Reports validation failures for a Configlue options value.</summary>
public sealed class ConfiglueValidationException : Exception
{
    /// <summary>Creates an exception containing the validation failures for an options value.</summary>
    public ConfiglueValidationException(
        string optionsName,
        Type optionsType,
        IEnumerable<string> failures
    )
        : this(
            optionsName,
            optionsType,
            failures?.ToArray() ?? throw new ArgumentNullException(nameof(failures))
        ) { }

    private ConfiglueValidationException(string optionsName, Type optionsType, string[] failures)
        : base(
            $"Options validation failed for '{optionsName}' ({optionsType}): {string.Join("; ", failures)}"
        )
    {
        ArgumentNullException.ThrowIfNull(optionsName);
        ArgumentNullException.ThrowIfNull(optionsType);
        if (failures.Length == 0)
        {
            throw new ArgumentException(
                "At least one validation failure is required.",
                nameof(failures)
            );
        }

        OptionsName = optionsName;
        OptionsType = optionsType;
        Failures = Array.AsReadOnly(failures);
    }

    /// <summary>The name of the options instance that failed validation.</summary>
    public string OptionsName { get; }

    /// <summary>The type of the options value that failed validation.</summary>
    public Type OptionsType { get; }

    /// <summary>The validation error messages.</summary>
    public IReadOnlyList<string> Failures { get; }
}
