using SparseFragments;

namespace SparseFragments.Tests;

public sealed class SchemaLookupTests
{
    [Test]
    public void GeneratedSchema_UsesZeroBasedContiguousOrdinals()
    {
        var schema = LookupSettings.Fragment.FragmentSchema;

        schema.HasOrdinalMemberIds.ShouldBeTrue();
        for (var index = 0; index < schema.Members.Count; index++)
        {
            schema.Members[index].Id.ShouldBe(index);
        }

        foreach (var expected in schema.Members)
        {
            schema.TryGetMember(expected.Id, out var actual).ShouldBeTrue();
            actual.Id.ShouldBe(expected.Id);
            actual.Name.ShouldBe(expected.Name);
        }
    }

    [Test]
    public void SchemaLookup_RejectsInvalidIdsDeterministically()
    {
        // LookupSettings has exactly two members (IDs 0-1).
        var schema = LookupSettings.Fragment.FragmentSchema;
        foreach (var invalidId in new[] { -1, 2, 99, int.MinValue, int.MaxValue })
        {
            schema.TryGetMember(invalidId, out _).ShouldBeFalse();
            Should.Throw<ArgumentException>(() => schema.GetMember(invalidId));
        }
    }

    [Test]
    public void TryGetMember_FallsBackForNonOrdinalSchemas()
    {
        var members = new[]
        {
            new SparseFragmentMemberSchema(4, "Fourth", typeof(int), MergeMode.Replace),
            new SparseFragmentMemberSchema(1, "Second", typeof(string), MergeMode.Replace),
        };
        var schema = new SparseFragmentSchema(typeof(LookupSettings), members);

        schema.HasOrdinalMemberIds.ShouldBeFalse();
        schema.TryGetMember(4, out var fourth).ShouldBeTrue();
        fourth.Name.ShouldBe("Fourth");
        schema.TryGetMember(1, out var second).ShouldBeTrue();
        second.Name.ShouldBe("Second");
        schema.GetMember(1).Name.ShouldBe("Second");
        foreach (var invalidId in new[] { -1, 0, 2, 3 })
        {
            schema.TryGetMember(invalidId, out _).ShouldBeFalse();
        }
    }
}

[SparseFragmentModel]
public partial class LookupSettings
{
    public int RetryCount { get; set; }

    public string? Label { get; set; }
}
