namespace Configlue;

/// <summary>Describes one present value in a generated sparse fragment.</summary>
/// <remarks>The default value is uninitialized; its name property safely returns an empty string.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly record struct ConfiglueFragmentMember
{
    private readonly string? _name;

    /// <summary>Gets or initializes this member's ordinal within its fragment's schema version.</summary>
    public int Id { get; init; }

    /// <summary>Gets or initializes the <see cref="Name"/> value.</summary>
    public string Name
    {
        get => _name ?? string.Empty;
        init => _name = value;
    }

    /// <summary>Whether this value is the uninitialized default fragment member.</summary>
    public bool IsDefault => string.IsNullOrWhiteSpace(_name);

    /// <summary>Gets or initializes the <see cref="Value"/> value.</summary>
    public object? Value { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Id">The initial value for the <see cref="Id"/> property.</param>
    /// <param name="Name">The initial value for the <see cref="Name"/> property.</param>
    /// <param name="Value">The initial value for the <see cref="Value"/> property.</param>
    public ConfiglueFragmentMember(int Id, string Name, object? Value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        this.Id = Id;
        this.Name = Name;
        this.Value = Value;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="Id">Receives the current <see cref="Id"/> value.</param>
    /// <param name="Name">Receives the current <see cref="Name"/> value.</param>
    /// <param name="Value">Receives the current <see cref="Value"/> value.</param>
    public void Deconstruct(out int Id, out string Name, out object? Value)
    {
        Id = this.Id;
        Name = this.Name;
        Value = this.Value;
    }
}

/// <summary>Non-generic fragment schema metadata used by codecs and runtime services.</summary>
/// <remarks>Application code should use the generated typed Fragment surface.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueFragment
{
    /// <summary>Generated schema metadata for this fragment.</summary>
    ConfiglueModelSchema Schema { get; }
}
