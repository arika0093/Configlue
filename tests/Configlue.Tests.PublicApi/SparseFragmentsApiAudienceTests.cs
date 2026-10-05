using System.ComponentModel;
using System.Reflection;

namespace Configlue.Tests.PublicApi;

/// <summary>
/// Guards the SparseFragments API boundary from issue #320.
/// Every public runtime type belongs to exactly one audience: first-class user API
/// (ordinary IntelliSense), extension/tooling API (<see cref="EditorBrowsableState.Advanced"/>),
/// or generated-code plumbing (<see cref="EditorBrowsableState.Never"/>).
/// </summary>
public sealed class SparseFragmentsApiAudienceTests
{
    private static readonly string[] FirstClass = [
        "SparseFragments.Optional`1",
        "SparseFragments.FragmentOperation`1",
        "SparseFragments.FragmentOperationKind",
        "SparseFragments.MergeMode",
        "SparseFragments.SparseFragmentModelAttribute",
        "SparseFragments.SparseMergeAttribute",
        "SparseFragments.SparseCloneReferenceSafeAttribute",
    ];

    private static readonly string[] Plumbing = [
        "SparseFragments.SparseValueComparer",
        "SparseFragments.SparseCollectionMerger",
        "SparseFragments.SparseCollectionRebase",
        "SparseFragments.SparseModelRebase",
        "SparseFragments.SparseReferenceEqualityComparer",
    ];

    [Test]
    public void EveryPublicTypeIsClassified()
    {
        var exported = typeof(SparseFragments.SparseFragmentModelAttribute).Assembly
            .GetExportedTypes()
            .Select(static type => type.FullName ?? type.Name)
            .OrderBy(static name => name)
            .ToArray();
        exported.ShouldNotBeEmpty();

        var offenders = exported
            .Where(static name => !FirstClass.Contains(name) && !IsAdvanced(name) && !Plumbing.Contains(name))
            .ToArray();
        offenders.ShouldBeEmpty();
    }

    [Test]
    public void FirstClassTypesStayVisible()
    {
        var offenders = FirstClass
            .Select(Resolve)
            .Where(static type => HasBrowsableHiding(type))
            .Select(static type => type.FullName ?? type.Name)
            .ToArray();
        offenders.ShouldBeEmpty();
    }

    [Test]
    public void ExtensionAndToolingTypesStayAdvanced()
    {
        var offenders = typeof(SparseFragments.SparseFragmentModelAttribute).Assembly
            .GetExportedTypes()
            .Where(static type => !FirstClass.Contains(type.FullName) && !Plumbing.Contains(type.FullName))
            .Where(static type => !HasAdvancedHiding(type))
            .Select(static type => type.FullName ?? type.Name)
            .OrderBy(static name => name)
            .ToArray();
        offenders.ShouldBeEmpty();
    }

    [Test]
    public void PlumbingTypesStayHidden()
    {
        var offenders = Plumbing
            .Select(Resolve)
            .Where(static type => !HasNeverHiding(type))
            .Select(static type => type.FullName ?? type.Name)
            .ToArray();
        offenders.ShouldBeEmpty();
    }

    private static bool IsAdvanced(string fullName) =>
        HasAdvancedHiding(Resolve(fullName));

    private static Type Resolve(string fullName) =>
        typeof(SparseFragments.SparseFragmentModelAttribute).Assembly.GetType(fullName)
        ?? throw new InvalidOperationException($"Unknown SparseFragments type '{fullName}'.");

    private static bool HasBrowsableHiding(MemberInfo member) =>
        member.GetCustomAttribute<EditorBrowsableAttribute>()
            is { State: not EditorBrowsableState.Always };

    private static bool HasAdvancedHiding(MemberInfo member) =>
        member.GetCustomAttribute<EditorBrowsableAttribute>()
            is { State: EditorBrowsableState.Advanced };

    private static bool HasNeverHiding(MemberInfo member) =>
        member.GetCustomAttribute<EditorBrowsableAttribute>()
            is { State: EditorBrowsableState.Never };
}
