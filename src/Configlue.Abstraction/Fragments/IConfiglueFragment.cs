namespace Configlue;

/// <summary>Describes one present value in a generated sparse fragment.</summary>
public readonly record struct ConfiglueFragmentMember(int Id, string Name, object? Value);

/// <summary>Non-generic access used by codecs and diagnostics on cold paths.</summary>
public interface IConfiglueFragment
{
    /// <summary>Generated schema metadata for this fragment.</summary>
    ConfiglueModelSchema Schema { get; }

    /// <summary>Enumerates only members present in this source contribution.</summary>
    IEnumerable<ConfiglueFragmentMember> EnumeratePresentMembers();

    /// <summary>Returns a copy with the specified member set to a present value.</summary>
    IConfiglueFragment WithMember(int memberId, object? value);
}
