using System.Buffers;
using System.Globalization;
using System.Text;
using Configlue.CompilerServices;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;

namespace Configlue.Tests;

public sealed class MemberLookupTests
{
    [Test]
    public void GeneratedSchemas_UseZeroBasedContiguousOrdinalsMatchingMembersOrder()
    {
        var schemas = new[]
        {
            MemberIdSchemaV1.ConfiglueSchema,
            MemberIdSchemaV2.ConfiglueSchema,
            MemberIdSchemaV3.ConfiglueSchema,
            MemberIdSchemaV4.ConfiglueSchema,
            AppSettings.ConfiglueSchema,
        };
        foreach (var schema in schemas)
        {
            schema.HasOrdinalMemberIds.ShouldBeTrue();
            for (var index = 0; index < schema.Members.Count; index++)
            {
                schema.Members[index].Id.ShouldBe(index);
            }
        }
    }

    [Test]
    public void TryGetMember_ResolvesEveryGeneratedMember()
    {
        var schema = AppSettings.ConfiglueSchema;
        foreach (var expected in schema.Members)
        {
            schema.TryGetMember(expected.Id, out var actual).ShouldBeTrue();
            actual.Id.ShouldBe(expected.Id);
            actual.Name.ShouldBe(expected.Name);
        }
    }

    [Test]
    public void MemberLookup_RejectsInvalidIdsDeterministically()
    {
        // MemberIdSchemaV2 has exactly three members (IDs 0-2).
        var schema = MemberIdSchemaV2.ConfiglueSchema;
        foreach (var invalidId in new[] { -100, -1, 3, 4, 100, int.MinValue, int.MaxValue })
        {
            schema.TryGetMember(invalidId, out var member).ShouldBeFalse();
            member.IsDefault.ShouldBeTrue();
            var exception = Should.Throw<ArgumentException>(() => schema.GetMember(invalidId));
            exception.Message.ShouldContain(invalidId.ToString(CultureInfo.InvariantCulture));
        }
    }

    [Test]
    public void TryGetMember_FallsBackForNonOrdinalSchemas()
    {
        var members = new[]
        {
            new ConfiglueMemberSchema(5, "Epsilon", typeof(int), MergeMode.Replace),
            new ConfiglueMemberSchema(2, "Beta", typeof(string), MergeMode.Replace),
        };
        var schema = new ConfiglueModelSchema(
            typeof(MemberIdSchemaV1),
            "tests.member-lookup-non-ordinal",
            1,
            members
        );

        schema.HasOrdinalMemberIds.ShouldBeFalse();
        schema.TryGetMember(5, out var epsilon).ShouldBeTrue();
        epsilon.Name.ShouldBe("Epsilon");
        schema.TryGetMember(2, out var beta).ShouldBeTrue();
        beta.Name.ShouldBe("Beta");
        schema.GetMember(2).Name.ShouldBe("Beta");
        foreach (var invalidId in new[] { -1, 0, 1, 3, 4 })
        {
            schema.TryGetMember(invalidId, out _).ShouldBeFalse();
        }
    }

    [Test]
    public void MemberPath_ResolvesThroughCentralizedLookup()
    {
        var schema = MemberIdSchemaV2.ConfiglueSchema;
        foreach (var expected in schema.Members)
        {
            var path = ConfiglueMemberPath.Root(schema).Append(expected.Id);
            var resolved = path.ResolveMember();
            resolved.Id.ShouldBe(expected.Id);
            resolved.Name.ShouldBe(expected.Name);
            path.ToString().ShouldBe(expected.Name);
        }

        var model = new MemberIdSchemaV2
        {
            A = 7,
            B = 8,
            C = 9,
        };
        var valuePath = ConfiglueMemberPath.Root(schema).Append(1);
        valuePath.GetModelValue(model, out var leaf).ShouldBe(8);
        leaf.Id.ShouldBe(1);
        leaf.Name.ShouldBe("B");

        Should.Throw<ArgumentException>(() =>
            ConfiglueMemberPath.Root(schema).Append(999).ResolveMember()
        );
    }

    [Test]
    public void XmlCodec_SkipsUnknownMemberIds()
    {
        var fragment = new MemberIdSchemaV1.Fragment
        {
            B = Optional<int>.Present(1),
            C = Optional<int>.Present(2),
        };
        var codec = new XmlStateCodec<MemberIdSchemaV1.Fragment>();
        var output = new ArrayBufferWriter<byte>();
        codec.Serialize(fragment, output, default);
        var xml = Encoding.UTF8.GetString(output.WrittenMemory.ToArray());
#if NETFRAMEWORK
        xml = xml.Replace("</configlue>", "<member id=\"999\" name=\"Bogus\" /></configlue>");
#else
        xml = xml.Replace(
            "</configlue>",
            "<member id=\"999\" name=\"Bogus\" /></configlue>",
            StringComparison.Ordinal
        );
#endif

        var restored = codec.Deserialize(
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(xml)),
            default
        );

        restored.ShouldNotBeNull();
        restored.B.ShouldBe(Optional<int>.Present(1));
        restored.C.ShouldBe(Optional<int>.Present(2));
    }

    [Test]
    public void YamlCodec_RoundTripsSparseFragmentThroughCentralizedLookup()
    {
        var fragment = new MemberIdSchemaV2.Fragment { B = Optional<int>.Present(3) };
        var codec = new YamlStateCodec<MemberIdSchemaV2.Fragment>(
            modelSchema: MemberIdSchemaV2.FragmentSchema
        );
        var output = new ArrayBufferWriter<byte>();
        codec.Serialize(fragment, output, default);

        var restored = codec.Deserialize(
            new ReadOnlySequence<byte>(output.WrittenMemory.ToArray()),
            default
        );

        restored.ShouldNotBeNull();
        restored.A.IsPresent.ShouldBeFalse();
        restored.B.ShouldBe(Optional<int>.Present(3));
        restored.C.IsPresent.ShouldBeFalse();
    }
}
