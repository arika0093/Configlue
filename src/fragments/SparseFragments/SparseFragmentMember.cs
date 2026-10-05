using System.ComponentModel;

namespace SparseFragments;

/// <summary>Describes one present value in a generated sparse fragment.</summary>
/// <remarks>Advanced tooling vocabulary: enumerated from <see cref="ISparseFragment"/>.</remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public readonly record struct SparseFragmentMember
{
    /// <summary>Gets or initializes the member ordinal within this fragment's schema version.</summary>
    /// <remarks>This value can change across schema versions and is not a persistent identifier.</remarks>
    public int Id { get; init; }

    /// <summary>Gets or initializes the <see cref="Name"/> value.</summary>
    public string Name { get; init; }

    /// <summary>Gets or initializes the <see cref="Value"/> value.</summary>
    public object? Value { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Id">The member ordinal within this fragment's schema version.</param>
    /// <param name="Name">The initial value for the <see cref="Name"/> property.</param>
    /// <param name="Value">The initial value for the <see cref="Value"/> property.</param>
    public SparseFragmentMember(int Id, string Name, object? Value)
    {
        this.Id = Id;
        this.Name = Name;
        this.Value = Value;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="Id">Receives the member ordinal within this fragment's schema version.</param>
    /// <param name="Name">Receives the current <see cref="Name"/> value.</param>
    /// <param name="Value">Receives the current <see cref="Value"/> value.</param>
    public void Deconstruct(out int Id, out string Name, out object? Value)
    {
        Id = this.Id;
        Name = this.Name;
        Value = this.Value;
    }
}
