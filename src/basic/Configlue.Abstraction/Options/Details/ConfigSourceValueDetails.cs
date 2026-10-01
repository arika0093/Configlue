namespace Configlue;

/// <summary>The contribution state of one source for one configuration member.</summary>
public enum ConfigSourceValueState
{
    /// <summary>The source contributes a present value.</summary>
    Present,

    /// <summary>The source was read and contributes no value for this member.</summary>
    Missing,

    /// <summary>The source could not be read for this snapshot.</summary>
    Unavailable,

    /// <summary>The source returned a malformed or undecodable payload (a source-local read outcome).</summary>
    Invalid,
}

/// <summary>One source's value state for one configuration member.</summary>
/// <typeparam name="T">The member value type.</typeparam>
public sealed class ConfigSourceValueDetails<T>
{
    /// <summary>Creates a per-source member snapshot.</summary>
    public ConfigSourceValueDetails(
        ConfigSourceDetails source,
        ConfigSourceValueState state,
        T? value
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        State = state;
        Value = value;
    }

    /// <summary>The source this entry describes.</summary>
    public ConfigSourceDetails Source { get; }

    /// <summary>The contribution state of the source for this member.</summary>
    public ConfigSourceValueState State { get; }

    /// <summary>Whether the source contributes a present value.</summary>
    public bool IsPresent => State == ConfigSourceValueState.Present;

    /// <summary>Whether this contribution is shadowed by a higher-priority present contribution.</summary>
    public bool IsShadowed { get; init; }

    /// <summary>The contributed value when present.</summary>
    public T? Value { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Source.DisplayName}: {State} = {Value}";
}
