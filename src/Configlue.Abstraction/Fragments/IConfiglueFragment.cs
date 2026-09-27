namespace Configlue;

/// <summary>Describes one present value in a generated sparse fragment.</summary>
public readonly record struct ConfiglueFragmentMember
{
    /// <summary>Gets or initializes the <see cref="Id"/> value.</summary>
    public int Id { get; init; }

    /// <summary>Gets or initializes the <see cref="Name"/> value.</summary>
    public string Name { get; init; }

    /// <summary>Gets or initializes the <see cref="Value"/> value.</summary>
    public object? Value { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Id">The initial value for the <see cref="Id"/> property.</param>
    /// <param name="Name">The initial value for the <see cref="Name"/> property.</param>
    /// <param name="Value">The initial value for the <see cref="Value"/> property.</param>
    public ConfiglueFragmentMember(int Id, string Name, object? Value)
    {
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

/// <summary>Non-generic access used by codecs and diagnostics on cold paths.</summary>
public interface IConfiglueFragment
{
    /// <summary>Generated schema metadata for this fragment.</summary>
    ConfiglueModelSchema Schema { get; }

    /// <summary>Enumerates only members present in this source contribution.</summary>
    IEnumerable<ConfiglueFragmentMember> EnumeratePresentMembers();

    /// <summary>Returns a copy with the specified member set to a present value.</summary>
    IConfiglueFragment WithMember(int memberId, object? value);

    /// <summary>Returns a copy with the specified member absent from this sparse contribution.</summary>
    IConfiglueFragment WithoutMember(int memberId) =>
        throw new NotSupportedException("This fragment does not support removing a member.");
}
