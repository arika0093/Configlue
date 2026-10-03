namespace Configlue.Tests;

public sealed class MemberIdStabilityTests
{
    [Test]
    public void MemberIdsAreOrdinalsWithinEachSchemaVersion()
    {
        Id(MemberIdSchemaV1.ConfiglueSchema, "B").ShouldBe(0);
        Id(MemberIdSchemaV1.ConfiglueSchema, "C").ShouldBe(1);

        Id(MemberIdSchemaV2.ConfiglueSchema, "A").ShouldBe(0);
        Id(MemberIdSchemaV2.ConfiglueSchema, "B").ShouldBe(1);
        Id(MemberIdSchemaV2.ConfiglueSchema, "C").ShouldBe(2);

        Id(MemberIdSchemaV3.ConfiglueSchema, "B").ShouldBe(0);
        Id(MemberIdSchemaV3.ConfiglueSchema, "C").ShouldBe(1);

        Id(MemberIdSchemaV4.ConfiglueSchema, "B").ShouldBe(0);
        Id(MemberIdSchemaV4.ConfiglueSchema, "C").ShouldBe(1);
    }

    private static int Id(ConfiglueModelSchema schema, string name) =>
        schema.Members.Single(member => member.Name == name).Id;
}

[ConfiglueModel("tests.member-ids-local", Version = 1)]
public partial class MemberIdSchemaV1
{
    public int B { get; set; }
    public int C { get; set; }
}

[ConfiglueModel("tests.member-ids-local", Version = 2)]
[ConfigluePreviousVersion(typeof(MemberIdSchemaV1))]
public partial class MemberIdSchemaV2
{
    public int A { get; set; }
    public int B { get; set; }
    public int C { get; set; }
}

[ConfiglueModel("tests.member-ids-local", Version = 3)]
[ConfigluePreviousVersion(typeof(MemberIdSchemaV2))]
public partial class MemberIdSchemaV3
{
    public int B { get; set; }
    public int C { get; set; }
}

[ConfiglueModel("tests.member-ids-local", Version = 4)]
[ConfigluePreviousVersion(typeof(MemberIdSchemaV3))]
public partial class MemberIdSchemaV4
{
    public int C { get; set; }
    public int B { get; set; }
}
