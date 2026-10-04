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

/// <summary>Generated dynamic member access optimized for indexed, allocation-free enumeration.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueOrdinalDynamicFragment : IConfiglueDynamicFragment
{
    /// <summary>Gets the number of currently present members.</summary>
    int PresentMemberCount { get; }

    /// <summary>Gets a present member by ordinal in the current fragment.</summary>
    ConfiglueFragmentMember GetPresentMember(int index);
}

/// <summary>A pattern-based present-member enumerator that avoids generated iterator allocations.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly struct ConfigluePresentMembers
{
    private readonly IConfiglueOrdinalDynamicFragment? _ordinal;
    private readonly IEnumerator<ConfiglueFragmentMember>? _fallback;

    internal ConfigluePresentMembers(IConfiglueFragment fragment)
    {
        if (fragment is IConfiglueOrdinalDynamicFragment ordinal)
        {
            _ordinal = ordinal;
            _fallback = null;
        }
        else
        {
            _ordinal = null;
            _fallback = ConfiglueDynamicFragmentExtensions
                .EnumeratePresentMembers(fragment)
                .GetEnumerator();
        }
    }

    /// <summary>Gets a struct enumerator for use by a foreach statement.</summary>
    public Enumerator GetEnumerator() => new(_ordinal, _fallback);

    /// <summary>Enumerator for present members.</summary>
    /// <remarks>Generated-code plumbing; hand-written code uses foreach.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public struct Enumerator : IDisposable
    {
        private readonly IConfiglueOrdinalDynamicFragment? _ordinal;
        private readonly IEnumerator<ConfiglueFragmentMember>? _fallback;
        private readonly int _count;
        private int _index;

        internal Enumerator(
            IConfiglueOrdinalDynamicFragment? ordinal,
            IEnumerator<ConfiglueFragmentMember>? fallback
        )
        {
            _ordinal = ordinal;
            _fallback = fallback;
            _count = ordinal?.PresentMemberCount ?? 0;
            _index = -1;
            Current = default;
        }

        /// <summary>The current member.</summary>
        public ConfiglueFragmentMember Current { get; private set; }

        /// <summary>Advances to the next present member.</summary>
        public bool MoveNext()
        {
            if (_ordinal is { } ordinal)
            {
                var next = _index + 1;
                if (next >= _count)
                {
                    return false;
                }

                _index = next;
                Current = ordinal.GetPresentMember(next);
                return true;
            }

            if (_fallback is { } fallback && fallback.MoveNext())
            {
                Current = fallback.Current;
                return true;
            }

            return false;
        }

        /// <summary>Releases a fallback dynamic enumerator when the fragment predates indexed access.</summary>
        public void Dispose() => _fallback?.Dispose();
    }
}

/// <summary>Advanced operations for code that explicitly works with runtime member IDs.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public static class ConfiglueDynamicFragmentExtensions
{
    /// <summary>Enumerates present members without allocating an iterator for generated fragments.</summary>
    public static ConfigluePresentMembers EnumeratePresentMembersFast(
        this IConfiglueFragment fragment
    )
    {
        ArgumentNullException.ThrowIfNull(fragment);
        return new ConfigluePresentMembers(fragment);
    }

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
