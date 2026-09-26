using Configlue;
using Configlue.Provider.Json;
using System.Buffers;
using System.Text;

namespace Configlue.Tests;

[ConfiglueModel(2, Id = "app-settings")]
public partial class AppSettings
{
    public bool Enabled { get; set; } = true;

    public int RetryCount { get; set; } = 3;

    public string? Label { get; set; } = "default";

    public DatabaseSettings? Database { get; set; } = new();

    [ConfiglueMerge(MergeMode.Append)]
    public IReadOnlyList<string> Plugins { get; set; } = [];
}

[ConfiglueModel]
public partial class DatabaseSettings
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;
}

public sealed class GeneratedFragmentTests
{
    [Test]
    public async Task SparseFragment_PreservesMissingPresentNullAndDefault()
    {
        var fragment = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Label = Optional<string?>.Present(null),
        };

        var value = fragment.ToModel();

        await Assert.That(fragment.Enabled.IsPresent).IsTrue();
        await Assert.That(fragment.Enabled.Value).IsFalse();
        await Assert.That(fragment.RetryCount.IsPresent).IsFalse();
        await Assert.That(fragment.Label.IsPresent).IsTrue();
        await Assert.That(fragment.Label.Value).IsNull();
        await Assert.That(value.Enabled).IsFalse();
        await Assert.That(value.RetryCount).IsEqualTo(3);
        await Assert.That(value.Database!.Host).IsEqualTo("localhost");
    }

    [Test]
    public async Task Merge_DeepMergesNestedModelsAndAppendsCollections()
    {
        var lower = new AppSettings.Fragment
        {
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Host = Optional<string>.Present("db.local"),
                Port = Optional<int>.Present(5432),
            }),
            Plugins = Optional<IReadOnlyList<string>>.Present(["base"]),
        };
        var higher = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Database = Optional<DatabaseSettings.Fragment?>.Present(new DatabaseSettings.Fragment
            {
                Port = Optional<int>.Present(6432),
            }),
            Plugins = Optional<IReadOnlyList<string>>.Present(["custom"]),
        };

        var merged = lower.Merge(higher).ToModel();

        await Assert.That(merged.Enabled).IsFalse();
        await Assert.That(merged.Database!.Host).IsEqualTo("db.local");
        await Assert.That(merged.Database.Port).IsEqualTo(6432);
        await Assert.That(merged.Plugins).IsEquivalentTo(["base", "custom"]);
    }

    [Test]
    public async Task SemanticDiffAndPatch_DistinguishSetFromUnset()
    {
        var before = new AppSettings { Enabled = true, RetryCount = 3, Label = "old" };
        var after = new AppSettings { Enabled = false, RetryCount = 3, Label = null };

        var diff = AppSettings.Fragment.Diff(before, after);
        var patched = AppSettings.Fragment.From(before).Apply(new AppSettings.Patch
        {
            Enabled = FragmentOperation<bool>.Set(false),
            RetryCount = FragmentOperation<int>.Unset,
        });

        await Assert.That(diff.Enabled.Value).IsFalse();
        await Assert.That(diff.RetryCount.IsPresent).IsFalse();
        await Assert.That(diff.Label.IsPresent).IsTrue();
        await Assert.That(diff.Label.Value).IsNull();
        await Assert.That(patched.Enabled.Value).IsFalse();
        await Assert.That(patched.RetryCount.IsPresent).IsFalse();
        await Assert.That(new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) }.IsEmpty).IsFalse();
        await Assert.That(AppSettings.ConfiglueSchema.Version).IsEqualTo(2);
        await Assert.That(AppSettings.ConfiglueSchema.Id).IsEqualTo("app-settings");
    }

    [Test]
    public async Task JsonCodec_RoundTripsSparsePresenceAndSchemaMetadata()
    {
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var context = new StateCodecContext(new StateSchemaMetadata("app-settings", 2));
        var fragment = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Label = Optional<string?>.Present(null),
        };
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();

        codec.Serialize(fragment, buffer, in context);
        var encoded = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        var decoded = codec.Deserialize(in sequence, default);
        var decodedFragment = decoded!;
        var schema = codec.ReadSchemaMetadata(in sequence);

        await Assert.That(encoded).Contains("\"$configlue\"");
        await Assert.That(encoded).Contains("\"Label\":null");
        await Assert.That(encoded).DoesNotContain("RetryCount");
        await Assert.That(decodedFragment.Enabled.IsPresent).IsTrue();
        await Assert.That(decodedFragment.Enabled.Value).IsFalse();
        await Assert.That(decodedFragment.Label.IsPresent).IsTrue();
        await Assert.That(decodedFragment.Label.Value).IsNull();
        await Assert.That(decodedFragment.RetryCount.IsPresent).IsFalse();
        await Assert.That(schema).IsEqualTo(new StateSchemaMetadata("app-settings", 2));
    }
}
