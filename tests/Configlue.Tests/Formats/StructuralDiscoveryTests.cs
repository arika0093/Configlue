using System.Buffers;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

[ConfiglueModel("structural-root", Version = 1)]
public partial class StructuralRoot
{
    public PlainDatabase Database { get; set; } = new();

    public NestedHolder Holder { get; set; } = new();

    public Uri? Endpoint { get; set; }

    [ConfiglueMerge(MergeMode.Replace)]
    public PlainConnection? Replaced { get; set; }
}

public class PlainDatabase
{
    public PlainConnection Connection { get; set; } = new();

    public int Timeout { get; set; } = 30;
}

public class PlainConnection
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;

    public string? Password { get; set; }
}

public class NestedHolder
{
    public PlainDatabase Database { get; set; } = new();
}

public struct PlainPoint
{
    public int X { get; set; }

    public int Y { get; set; }
}

[ConfiglueModel("scalar-struct-root", Version = 1)]
public partial class ScalarStructRoot
{
    public PlainPoint Origin { get; set; }
}

[ConfiglueModel("nested-declaration-root", Version = 1)]
public partial class NestedDeclarationRoot
{
    public DatabaseOptions Database { get; set; } = new();

    public class DatabaseOptions
    {
        public CredentialsOptions Credentials { get; set; } = new();

        public class CredentialsOptions
        {
            public string User { get; set; } = string.Empty;
        }
    }
}

[ConfiglueModel("point-settings", Version = 1)]
public partial struct PointSettings
{
    public int X { get; set; }

    public int Y { get; set; }
}

[ConfiglueModel("struct-holder", Version = 1)]
public partial class StructHolder
{
    public PointSettings Point { get; set; }
}

public class Node
{
    public int Value { get; set; }

    public Node? Next { get; set; }
}

[ConfiglueModel("cycle-root", Version = 1)]
public partial class CycleRoot
{
    public Node Head { get; set; } = new();

    public Node Shared { get; set; } = new();
}

public sealed class StructuralDiscoveryTests
{
    private static StructuralRoot.Patch Patch() => new();

    [Test]
    public void PlainPoco_DeepMerge_PreservesUntouchedNestedSiblings()
    {
        var lowerPatch = Patch();
        lowerPatch.Database.Connection.Host = FragmentOperation<string>.Set("lower.host");
        var lower = StructuralRoot.Fragment.Empty.Apply(lowerPatch);

        var higherPatch = Patch();
        higherPatch.Database.Connection.Port = FragmentOperation<int>.Set(6432);
        var higher = StructuralRoot.Fragment.Empty.Apply(higherPatch);

        var merged = lower.Merge(higher).ToModel();

        merged.Database.Connection.Host.ShouldBe("lower.host");
        merged.Database.Connection.Port.ShouldBe(6432);
        merged.Database.Timeout.ShouldBe(30);
    }

    [Test]
    public void PlainPoco_MultiLevel_DiscoveryAndRoundTrip()
    {
        var fragment = StructuralRoot.Fragment.From(
            new StructuralRoot
            {
                Database = new PlainDatabase
                {
                    Connection = new PlainConnection { Host = "db.local", Port = 1234 },
                    Timeout = 45,
                },
            }
        );

        var model = fragment.ToModel();

        model.Database.Connection.Host.ShouldBe("db.local");
        model.Database.Connection.Port.ShouldBe(1234);
        model.Database.Timeout.ShouldBe(45);
    }

    [Test]
    public void PlainPoco_DiffAndApplyChanges_TrackNestedValues()
    {
        var before = new StructuralRoot
        {
            Database = new PlainDatabase
            {
                Connection = new PlainConnection { Host = "a", Port = 1 },
                Timeout = 10,
            },
        };
        var after = new StructuralRoot
        {
            Database = new PlainDatabase
            {
                Connection = new PlainConnection { Host = "b", Port = 1 },
                Timeout = 10,
            },
        };

        var diff = StructuralRoot.Fragment.Diff(before, after);
        diff.Database.IsPresent.ShouldBeTrue();

        var applied = StructuralRoot.Fragment.From(before).ApplyChanges(diff).ToModel();

        applied.Database.Connection.Host.ShouldBe("b");
        applied.Database.Connection.Port.ShouldBe(1);
        applied.Database.Timeout.ShouldBe(10);
    }

    [Test]
    public void PlainPoco_NullTransitions_AreRepresentable()
    {
        var valuePatch = Patch();
        valuePatch.Database.Set(
            new PlainDatabase { Connection = new PlainConnection { Host = "new" } }
        );
        var withValue = StructuralRoot.Fragment.Empty.Apply(valuePatch).ToModel();
        withValue.Database.Connection.Host.ShouldBe("new");

        var fragment = StructuralRoot.Fragment.From(
            new StructuralRoot { Database = new PlainDatabase() }
        );
        var nullPatch = Patch();
        nullPatch.Database.SetNull();
        var withNull = fragment.Apply(nullPatch).ToModel();
        withNull.Database.ShouldBeNull();
    }

    [Test]
    public void ExplicitReplace_OptOut_ReplacesNestedObject()
    {
        var lowerPatch = Patch();
        lowerPatch.Replaced.Set(new PlainConnection { Host = "lower", Port = 1111 });
        var lower = StructuralRoot.Fragment.Empty.Apply(lowerPatch);

        var higherPatch = Patch();
        higherPatch.Replaced.Set(new PlainConnection { Host = "localhost", Port = 2222 });
        var higher = StructuralRoot.Fragment.Empty.Apply(higherPatch);

        var merged = lower.Merge(higher).ToModel();

        merged.Replaced!.Host.ShouldBe("localhost");
        merged.Replaced.Port.ShouldBe(2222);
    }

    [Test]
    public void PlainPoco_JsonRoundTrip_PreservesSparseNestedPresence()
    {
        var codec = new JsonStateCodec<StructuralRoot.Fragment>();
        var context = new StateCodecContext(new StateSchemaMetadata("structural-root", 1));
        var patch = Patch();
        patch.Database.Connection.Host = FragmentOperation<string>.Set("json.host");
        var fragment = StructuralRoot.Fragment.Empty.Apply(patch);

        var buffer = new ArrayBufferWriter<byte>();
        codec.Serialize(fragment, buffer, in context);
        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        var decoded = codec.Deserialize(in sequence, default)!;

        decoded.Database.IsPresent.ShouldBeTrue();
        decoded.Database.Value!.Connection.IsPresent.ShouldBeTrue();
        decoded.Database.Value.Connection.Value!.Host.ShouldBe(Optional<string>.Present("json.host"));
        decoded.Database.Value.Connection.Value.Port.IsPresent.ShouldBeFalse();

        var model = decoded.ToModel();
        model.Database.Connection.Host.ShouldBe("json.host");
        model.Database.Connection.Port.ShouldBe(5432);
    }

    [Test]
    public void NestedTypeDeclarations_AreDeepCloned()
    {
        var original = new NestedDeclarationRoot
        {
            Database = new NestedDeclarationRoot.DatabaseOptions
            {
                Credentials = new NestedDeclarationRoot.DatabaseOptions.CredentialsOptions
                {
                    User = "original",
                },
            },
        };

        var clone = ((IConfiglueDeepCloneable<NestedDeclarationRoot>)original).DeepClone();
        clone.Database.ShouldNotBeSameAs(original.Database);
        clone.Database.Credentials.ShouldNotBeSameAs(original.Database.Credentials);
        clone.Database.Credentials.User.ShouldBe("original");

        clone.Database.Credentials.User = "clone";
        original.Database.Credentials.User.ShouldBe("original");
    }

    [Test]
    public void NestedConfiglueStruct_BehavesLikeRootStruct()
    {
        var fragment = StructHolder.Fragment.From(
            new StructHolder { Point = new PointSettings { X = 3, Y = 4 } }
        );
        var patch = new StructHolder.Patch();
        patch.Point.X = FragmentOperation<int>.Set(7);
        var patched = fragment.Apply(patch).ToModel();

        patched.Point.X.ShouldBe(7);
        patched.Point.Y.ShouldBe(4);
    }

    [Test]
    public void OrdinaryStruct_IsTreatedAsScalar()
    {
        var member = ScalarStructRoot
            .FragmentSchema.Members.Single(candidate => candidate.Name == "Origin");
        member.NestedSchemaFactory.ShouldBeNull();
        member.MergeMode.ShouldBe(Configlue.MergeMode.Replace);
    }

    [Test]
    public void PlainPoco_IsRegisteredAsNestedSchema()
    {
        var database = StructuralRoot
            .FragmentSchema.Members.Single(candidate => candidate.Name == "Database");
        database.MergeMode.ShouldBe(Configlue.MergeMode.Deep);
        database.NestedSchemaFactory.ShouldNotBeNull();
        database.NestedSchemaFactory!().ModelType.ShouldBe(typeof(PlainDatabase));
    }

    [Test]
    public async Task PlainPoco_DetailsExposeNestedProvenance()
    {
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<StructuralRoot>(model =>
                model.Sources(sources =>
                {
                    var patch = Patch();
                    patch.Database.Connection.Host = FragmentOperation<string>.Set("detail.host");
                    sources.Add(
                        new StateSource<StructuralRoot.Fragment>("base", new InMemoryStateSource<StructuralRoot.Fragment>(
                                StructuralRoot.Fragment.Empty.Apply(patch)
                            ), new StateSourceOptions<StructuralRoot.Fragment>())
                    );
                })
            );
        });

        var options = context.GetState<StructuralRoot>();
        var details = await options.GetDetailsAsync();

        details.Database.ShouldNotBeNull();
        details.Database!.Connection.ShouldNotBeNull();
        details.Database.Connection!.Host.Value.ShouldBe("detail.host");
        details.Database.Connection.Host.Source.ShouldNotBeNull();
        details.Database.Connection.Port.Value.ShouldBe(5432);
    }

    [Test]
    public void OpaqueFrameworkType_IsNotDecomposed()
    {
        var member = StructuralRoot
            .FragmentSchema.Members.Single(candidate => candidate.Name == "Endpoint");
        member.NestedSchemaFactory.ShouldBeNull();
        member.MergeMode.ShouldBe(Configlue.MergeMode.Replace);
    }

    [Test]
    public void PocoClone_PreservesSharedReferencesAndCycles()
    {
        var shared = new Node { Value = 1 };
        var root = new CycleRoot { Head = new Node { Value = 2, Next = shared }, Shared = shared };
        root.Head.Next = shared;

        var clone = ((IConfiglueDeepCloneable<CycleRoot>)root).DeepClone();

        clone.Shared.ShouldNotBeSameAs(shared);
        clone.Head.Next.ShouldBeSameAs(clone.Shared);

        var cyclic = new Node { Value = 9 };
        cyclic.Next = cyclic;
        var cycleRoot = new CycleRoot { Head = cyclic, Shared = cyclic };
        var cycleClone = ((IConfiglueDeepCloneable<CycleRoot>)cycleRoot).DeepClone();

        cycleClone.Head.ShouldNotBeSameAs(cyclic);
        cycleClone.Head.Next.ShouldBeSameAs(cycleClone.Head);
        cycleClone.Head.Next!.Value.ShouldBe(9);
    }
}
