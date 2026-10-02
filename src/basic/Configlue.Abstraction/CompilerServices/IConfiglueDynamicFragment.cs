namespace Configlue.CompilerServices;

/// <summary>Advanced runtime member-ID operations on generated fragments.</summary>
/// <remarks>
/// Application code should use generated typed Fragment and Patch members. This contract is for
/// codecs, runtime routing, diagnostics, and other tools that resolve members dynamically.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueDynamicFragment : IConfiglueFragment
{
    /// <summary>Enumerates present member IDs, names, and values for runtime tooling.</summary>
    IEnumerable<ConfiglueFragmentMember> EnumeratePresentMembers();

    /// <summary>Returns a copy with the specified generated member set to a present value.</summary>
    IConfiglueFragment WithMember(int memberId, object? value);

    /// <summary>Returns a copy with the specified generated member absent.</summary>
    IConfiglueFragment WithoutMember(int memberId);
}

/// <summary>Advanced operations for code that explicitly works with runtime member IDs.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public static class ConfiglueDynamicFragmentExtensions
{
    /// <summary>Enumerates present members on the dynamic runtime surface.</summary>
    public static IEnumerable<ConfiglueFragmentMember> EnumeratePresentMembers(
        this IConfiglueFragment fragment
    ) => GetDynamicFragment(fragment).EnumeratePresentMembers();

    /// <summary>Sets a fragment member by generated ID on the dynamic runtime surface.</summary>
    public static IConfiglueFragment WithMember(
        this IConfiglueFragment fragment,
        int memberId,
        object? value
    ) => GetDynamicFragment(fragment).WithMember(memberId, value);

    /// <summary>Unsets a fragment member by generated ID on the dynamic runtime surface.</summary>
    public static IConfiglueFragment WithoutMember(
        this IConfiglueFragment fragment,
        int memberId
    ) => GetDynamicFragment(fragment).WithoutMember(memberId);

    private static IConfiglueDynamicFragment GetDynamicFragment(IConfiglueFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        return fragment as IConfiglueDynamicFragment
            ?? throw new NotSupportedException(
                $"Fragment '{fragment.GetType()}' does not expose dynamic member operations."
            );
    }
}
