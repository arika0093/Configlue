using System.Buffers;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Xml.Linq;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Testing;

namespace Configlue.Tests;

[ConfiglueModel("app-settings", Version = 2)]
public partial class AppSettings
{
    public bool Enabled { get; set; } = true;

    [Range(0, 100)]
    public int RetryCount { get; set; } = 3;

    [ConfiglueEnvironment("APP_SETTINGS_LABEL")]
    public string? Label { get; set; } = "default";

    public DatabaseSettings? Database { get; set; } = new();

    [ConfiglueMerge(MergeMode.Append)]
    public IReadOnlyList<string> Plugins { get; set; } = [];
}

[ConfiglueModel("clr-default-settings", Version = 1)]
public partial class ClrDefaultSettings
{
    public int RetryCount { get; set; }

    public bool Enabled { get; set; }

    public string? Name { get; set; }
}

[ConfiglueModel("database-settings", Version = 2)]
public partial class DatabaseSettings
{
    [ConfiglueEnvironment("DATABASE_HOST")]
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;
}

[ConfiglueModel("set-union-settings", Version = 1)]
public partial class SetUnionSettings
{
    [ConfiglueMerge(MergeMode.SetUnion)]
    public IReadOnlyList<string> Tags { get; set; } = [];
}

[ConfiglueModel("historical-settings", Version = 1)]
public partial class HistoricalSettingsV1
{
    public int RetryCount { get; set; } = 3;

    public string? NullableLabel { get; set; } = "legacy-default";

    public string? OldName { get; set; } = "legacy-name";
}

[ConfiglueModel("historical-settings", Version = 2)]
public partial class HistoricalSettingsV2
{
    public int RetryCount { get; set; } = 4;

    public string? NullableLabel { get; set; } = "v2-default";

    public string? NewName { get; set; } = "v2-name";
}

[ConfiglueModel("historical-settings", Version = 3)]
[ConfigluePreviousVersion(typeof(HistoricalSettingsV1))]
[ConfigluePreviousVersion(typeof(HistoricalSettingsV2))]
public partial class HistoricalSettings
{
    public int RetryCount { get; set; } = 5;

    public string? NullableLabel { get; set; } = "current-default";

    public string? NewName { get; set; } = "current-name";

    public bool Enabled { get; set; } = true;
}

[ConfiglueModel("inner-settings", Version = 1)]
public partial class InnerSettingsV1
{
    public int Count { get; set; } = 1;
}

[ConfiglueModel("inner-settings", Version = 2)]
[ConfigluePreviousVersion(typeof(InnerSettingsV1))]
public partial class InnerSettingsV2
{
    public int Count { get; set; } = 2;

    public string? Note { get; set; } = "note";
}

[ConfiglueModel("nested-settings", Version = 1)]
public partial class NestedSettingsV1
{
    public string? Label { get; set; } = "v1";

    public InnerSettingsV1? Inner { get; set; } = new();
}

[ConfiglueModel("nested-settings", Version = 2)]
[ConfigluePreviousVersion(typeof(NestedSettingsV1))]
public partial class NestedSettings
{
    public string? Label { get; set; } = "v2";

    public InnerSettingsV2? Inner { get; set; } = new();
}

[ConfiglueModel("ownership-child", Version = 1)]
public partial class OwnershipChild
{
    public string Name { get; set; } = "";
}

[ConfiglueModel("ownership-settings", Version = 1)]
public partial class OwnershipSettings
{
    public string[] ArrayValues { get; set; } = [];

    public List<string> ListValues { get; set; } = [];

    public ISet<string> SetValues { get; set; } = new HashSet<string>();

    public List<OwnershipChild> Children { get; set; } = [];
}

[ConfiglueModel("xml-collection-shapes", Version = 1)]
public partial class XmlCollectionShapesSettings
{
    public int[] ArrayValues { get; set; } = [];
    public List<int> ListValues { get; set; } = [];
    public IReadOnlyList<int> ReadOnlyListValues { get; set; } = [];
    public HashSet<int> HashSetValues { get; set; } = [];
    public ISet<int> SetValues { get; set; } = new HashSet<int>();
    public IReadOnlySet<int> ReadOnlySetValues { get; set; } = new TestReadOnlySet<int>([]);
    public Dictionary<string, int> DictionaryValues { get; set; } = [];
    public IReadOnlyDictionary<string, int> ReadOnlyDictionaryValues { get; set; } = new Dictionary<string, int>();
    public Queue<int> QueueValues { get; set; } = new();
    public ReadOnlyCollection<int> ReadOnlyCollectionValues { get; set; } = Array.AsReadOnly(Array.Empty<int>());
    public ImmutableArray<int> ImmutableArrayValues { get; set; } = [];
    public ImmutableList<int> ImmutableListValues { get; set; } = [];
    public ImmutableHashSet<int> ImmutableSetValues { get; set; } = [];
    public ImmutableDictionary<string, int> ImmutableDictionaryValues { get; set; } = ImmutableDictionary<string, int>.Empty;
}

[ConfiglueModel("xml-null-dictionary", Version = 1)]
public partial class XmlNullDictionarySettings
{
    public Dictionary<string, List<int>?> Values { get; set; } = new();

    public Dictionary<string, int?> NullableInts { get; set; } = new();

    public Dictionary<string, string?> NullableStrings { get; set; } = new();

    public Dictionary<string, Dictionary<string, int>?> Nested { get; set; } = new();
}

[ConfiglueModel("xml-nonnull-dictionary", Version = 1)]
public partial class XmlNonNullDictionarySettings
{
    public Dictionary<string, int> Values { get; set; } = new();
}

public sealed class TestReadOnlySet<T>(IEnumerable<T> values) : IReadOnlySet<T>
{
    private readonly HashSet<T> _values = new(values);
    public int Count => _values.Count;
    public bool Contains(T item) => _values.Contains(item);
    public bool IsProperSubsetOf(IEnumerable<T> other) => _values.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<T> other) => _values.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<T> other) => _values.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<T> other) => _values.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<T> other) => _values.Overlaps(other);
    public bool SetEquals(IEnumerable<T> other) => _values.SetEquals(other);
    public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class GeneratedFragmentTests
{
    // Owns generated fragment presence/merge/diff/patch/builder semantics and
    // historical migration dispatch, plus the shared generated models below.
    // Codec binding for these models lives in GeneratedFragmentCodecTests.
    [Test]
    public async Task SparseFragment_PreservesMissingPresentNullAndDefault()
    {
        var fragment = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Label = Optional<string?>.Present(null),
        };

        var value = fragment.ToModel();

        (fragment.Enabled.IsPresent).ShouldBeTrue();
        (fragment.Enabled.Value).ShouldBeFalse();
        (fragment.RetryCount.IsPresent).ShouldBeFalse();
        (fragment.Label.IsPresent).ShouldBeTrue();
        (fragment.Label.Value).ShouldBeNull();
        (value.Enabled).ShouldBeFalse();
        (value.RetryCount).ShouldBe(3);
        (value.Database!.Host).ShouldBe("localhost");
    }

    [Test]
    public void FragmentFromModel_CopiesMutableCollectionMembers()
    {
        var plugins = new List<string> { "before-save" };
        var model = new AppSettings { Plugins = plugins };

        var fragment = AppSettings.Fragment.From(model);
        plugins.Add("after-save");

        fragment.Plugins.Value.ShouldBe(new[] { "before-save" });
    }

    [Test]
    public void FragmentFromModel_DeepCopiesSupportedCollectionsAndGeneratedElements()
    {
        var child = new OwnershipChild { Name = "before-save" };
        var model = new OwnershipSettings
        {
            ArrayValues = ["array-before"],
            ListValues = ["list-before"],
            SetValues = new HashSet<string> { "set-before" },
            Children = [child],
        };

        var fragment = OwnershipSettings.Fragment.From(model);
        model.ArrayValues[0] = "array-after";
        model.ListValues.Add("list-after");
        model.SetValues.Add("set-after");
        model.Children.Add(new OwnershipChild { Name = "later" });
        child.Name = "mutated-child";

        fragment.ArrayValues.Value.ShouldBe(new[] { "array-before" });
        fragment.ListValues.Value.ShouldBe(new[] { "list-before" });
        fragment.SetValues.Value.ShouldBe(new HashSet<string> { "set-before" });
        fragment.Children.Value!.Select(static item => item.Name).ShouldBe(["before-save"]);
    }

    [Test]
    public async Task Merge_DeepMergesNestedModelsAndAppendsCollections()
    {
        var lower = new AppSettings.Fragment
        {
            Database = Optional<DatabaseSettings.Fragment?>.Present(
                new DatabaseSettings.Fragment
                {
                    Host = Optional<string>.Present("db.local"),
                    Port = Optional<int>.Present(5432),
                }
            ),
            Plugins = Optional<IReadOnlyList<string>>.Present(["base"]),
        };
        var higher = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Database = Optional<DatabaseSettings.Fragment?>.Present(
                new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
            ),
            Plugins = Optional<IReadOnlyList<string>>.Present(["custom"]),
        };

        var merged = lower.Merge(higher).ToModel();

        (merged.Enabled).ShouldBeFalse();
        (merged.Database!.Host).ShouldBe("db.local");
        (merged.Database.Port).ShouldBe(6432);
        ((merged.Plugins))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "base", "custom" }).OrderBy(static item => item));
    }

    [Test]
    public async Task SemanticDiffAndPatch_DistinguishSetFromUnset()
    {
        var before = new AppSettings
        {
            Enabled = true,
            RetryCount = 3,
            Label = "old",
        };
        var after = new AppSettings
        {
            Enabled = false,
            RetryCount = 3,
            Label = null,
        };

        var diff = AppSettings.Fragment.Diff(before, after);
        var patched = AppSettings
            .Fragment.From(before)
            .Apply(
                new AppSettings.Patch
                {
                    Enabled = FragmentOperation<bool>.Set(false),
                    RetryCount = FragmentOperation<int>.Unset,
                }
            );

        (diff.Enabled.Value).ShouldBeFalse();
        (diff.RetryCount.IsPresent).ShouldBeFalse();
        (diff.Label.IsPresent).ShouldBeTrue();
        (diff.Label.Value).ShouldBeNull();
        (patched.Enabled.Value).ShouldBeFalse();
        (patched.RetryCount.IsPresent).ShouldBeFalse();
        (
            new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) }.IsEmpty
        ).ShouldBeFalse();
        (AppSettings.ConfiglueSchema.Version).ShouldBe(2);
        (AppSettings.ConfiglueSchema.Id).ShouldBe("app-settings");
    }

    [Test]
    public async Task GeneratedHistoricalMapper_PreservesPresenceForCompatibleMembers()
    {
        var previous = new HistoricalSettingsV1.Fragment
        {
            RetryCount = Optional<int>.Present(0),
            NullableLabel = Optional<string?>.Present(null),
        };

        var migrated = HistoricalSettings.Fragment.FromPrevious(previous);

        (migrated.RetryCount.IsPresent).ShouldBeTrue();
        (migrated.RetryCount.Value).ShouldBe(0);
        (migrated.NullableLabel.IsPresent).ShouldBeTrue();
        (migrated.NullableLabel.Value).ShouldBeNull();
        (migrated.NewName.IsPresent).ShouldBeFalse();
        (migrated.ToModel().Enabled).ShouldBeTrue();
    }

    [Test]
    public async Task FragmentBuilder_CopyFromUnsetAndSetPreservePresence()
    {
        var previous = new HistoricalSettingsV1.Fragment
        {
            RetryCount = Optional<int>.Present(0),
            NullableLabel = Optional<string?>.Present(null),
            OldName = Optional<string?>.Present("renamed"),
        };
        var builder = HistoricalSettings.Fragment.FromPrevious(previous).ToBuilder();

        builder.NewName.CopyFrom(previous.OldName);
        builder.RetryCount.Set(12);
        builder.NullableLabel.Unset();

        var migrated = builder.Build();

        (migrated.NewName.IsPresent).ShouldBeTrue();
        (migrated.NewName.Value).ShouldBe("renamed");
        (migrated.RetryCount.IsPresent).ShouldBeTrue();
        (migrated.RetryCount.Value).ShouldBe(12);
        (migrated.NullableLabel.IsPresent).ShouldBeFalse();
    }

    [Test]
    public async Task FragmentBuilder_CopyFromMissingStaysMissingAndNullStaysPresentNull()
    {
        var builder = new HistoricalSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
            NullableLabel = Optional<string?>.Present("value"),
        }.ToBuilder();
        var missing = new HistoricalSettingsV1.Fragment();
        var presentNull = new HistoricalSettingsV1.Fragment
        {
            RetryCount = Optional<int>.Present(0),
            NullableLabel = Optional<string?>.Present(null),
        };

        builder.RetryCount.CopyFrom(missing.RetryCount);
        builder.NullableLabel.CopyFrom(presentNull.NullableLabel);

        (builder.RetryCount.IsPresent).ShouldBeFalse();
        (builder.NullableLabel.IsPresent).ShouldBeTrue();
        (builder.NullableLabel.Value).ShouldBeNull();
    }

    [Test]
    public async Task Patch_AssignmentHelpersMatchExplicitOperations()
    {
        var current = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(true),
            Label = Optional<string?>.Present("old"),
        };
        var patch = new AppSettings.Patch();
        patch.Enabled = false;
        patch.Label.Unset();

        var patched = current.Apply(patch);
        var disabled = FragmentOperation<bool>.Set(true);
        disabled.Unset();

        (patched.Enabled.IsPresent).ShouldBeTrue();
        (patched.Enabled.Value).ShouldBeFalse();
        (patched.Label.IsPresent).ShouldBeFalse();
        (disabled.Kind).ShouldBe(FragmentOperationKind.Unset);
    }

    [Test]
    public void PatchCopyFrom_PreservesMissingNullDefaultAndCollectionValues()
    {
        var current = new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(8),
            Label = Optional<string?>.Present("old"),
            Plugins = Optional<IReadOnlyList<string>>.Present(["old"]),
        };
        var source = new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(0),
            Label = Optional<string?>.Present(null),
            Plugins = Optional<IReadOnlyList<string>>.Present(["new"]),
        };
        var patch = new AppSettings.Patch();

        patch.Enabled.CopyFrom(Optional<bool>.Present(false));
        patch.RetryCount.CopyFrom(source.RetryCount);
        patch.Label.CopyFrom(source.Label);
        patch.Plugins.CopyFrom(source.Plugins);
        patch.Database.Unset();

        var patched = current.Apply(patch);

        patched.Enabled.Value.ShouldBeFalse();
        patched.RetryCount.Value.ShouldBe(0);
        patched.Label.IsPresent.ShouldBeTrue();
        patched.Label.Value.ShouldBeNull();
        patched.Plugins.Value.ShouldBe(new[] { "new" });
        patched.Database.IsPresent.ShouldBeFalse();
    }

    [Test]
    public void PatchCopyFrom_ConvertsValuesAndKeepsPresenceSemantics()
    {
        var current = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(true),
            RetryCount = Optional<int>.Present(5),
            Label = Optional<string?>.Present("old"),
            Plugins = Optional<IReadOnlyList<string>>.Present(["old"]),
        };
        var patch = new AppSettings.Patch();

        patch.Enabled.CopyFrom(Optional<int>.Missing, static value => value != 0);
        patch.RetryCount.CopyFrom(
            Optional<string?>.Present(null),
            static value => value?.Length ?? 0
        );
        patch.Label.CopyFrom(Optional<int>.Present(0), static value => value.ToString());
        patch.Plugins.CopyFrom(
            Optional<string?>.Present("converted"),
            static value => new[] { value! }
        );

        var patched = current.Apply(patch);

        patched.Enabled.IsPresent.ShouldBeFalse();
        patched.RetryCount.IsPresent.ShouldBeTrue();
        patched.RetryCount.Value.ShouldBe(0);
        patched.Label.Value.ShouldBe("0");
        patched.Plugins.Value.ShouldBe(new[] { "converted" });
    }

    [Test]
    public async Task FromPrevious_NestedMemberMigrationPreservesPresence()
    {
        var value = new NestedSettingsV1.Fragment
        {
            Label = Optional<string?>.Present("kept"),
            Inner = Optional<InnerSettingsV1.Fragment?>.Present(
                new InnerSettingsV1.Fragment { Count = Optional<int>.Present(0) }
            ),
        };
        var presentNull = new NestedSettingsV1.Fragment
        {
            Inner = Optional<InnerSettingsV1.Fragment?>.Present(null),
        };
        var missing = new NestedSettingsV1.Fragment();

        var migratedValue = NestedSettings.Fragment.FromPrevious(value);
        var migratedNull = NestedSettings.Fragment.FromPrevious(presentNull);
        var migratedMissing = NestedSettings.Fragment.FromPrevious(missing);

        (migratedValue.Label.IsPresent).ShouldBeTrue();
        (migratedValue.Label.Value).ShouldBe("kept");
        (migratedValue.Inner.IsPresent).ShouldBeTrue();
        (migratedValue.Inner.Value).ShouldNotBeNull();
        (migratedValue.Inner.Value!.Count.IsPresent).ShouldBeTrue();
        (migratedValue.Inner.Value!.Count.Value).ShouldBe(0);
        (migratedValue.Inner.Value!.Note.IsPresent).ShouldBeFalse();

        (migratedNull.Inner.IsPresent).ShouldBeTrue();
        (migratedNull.Inner.Value).ShouldBeNull();

        (migratedMissing.Inner.IsPresent).ShouldBeFalse();
    }

    [Test]
    public async Task FromPrevious_NestedMigrationReachesMaterializedModel()
    {
        var value = new NestedSettingsV1.Fragment
        {
            Inner = Optional<InnerSettingsV1.Fragment?>.Present(
                new InnerSettingsV1.Fragment { Count = Optional<int>.Present(42) }
            ),
        };

        var model = NestedSettings.Fragment.FromPrevious(value).ToModel();

        (model.Inner).ShouldNotBeNull();
        (model.Inner!.Count).ShouldBe(42);
        (model.Inner.Note).ShouldBe("note");
        (model.Label).ShouldBe("v2");
    }

    [Test]
    public async Task SerializedSource_DispatchesHistoricalFragmentBeforeOptionsMaterialize()
    {
        var (fragment, model) = await ReadHistoricalSettingsAsync(
            new JsonStateCodec<HistoricalSettingsV1.Fragment>(),
            new JsonStateCodec<HistoricalSettings.Fragment>()
        );

        (fragment.RetryCount.IsPresent).ShouldBeTrue();
        (fragment.RetryCount.Value).ShouldBe(0);
        (fragment.NullableLabel.IsPresent).ShouldBeTrue();
        (fragment.NullableLabel.Value).ShouldBeNull();
        (fragment.NewName.IsPresent).ShouldBeTrue();
        (fragment.NewName.Value).ShouldBe("renamed");
        (model.RetryCount).ShouldBe(0);
        (model.NullableLabel).ShouldBeNull();
        (model.NewName).ShouldBe("renamed");
        (model.Enabled).ShouldBeTrue();
    }

    [Test]
    public async Task XmlCodec_DispatchesHistoricalGeneratedFragment()
    {
        var (_, model) = await ReadHistoricalSettingsAsync(
            new XmlStateCodec<HistoricalSettingsV1.Fragment>(),
            new XmlStateCodec<HistoricalSettings.Fragment>()
        );

        (model.RetryCount).ShouldBe(0);
        (model.NullableLabel).ShouldBeNull();
        (model.NewName).ShouldBe("renamed");
    }

    [Test]
    public async Task SerializedSource_DispatchesEachDeclaredHistoricalVersion()
    {
        var resource = new InMemoryResource();
        var oldCodec = new JsonStateCodec<HistoricalSettingsV2.Fragment>();
        var writer = new SerializedStateWriter<HistoricalSettingsV2.Fragment>(resource, oldCodec);
        await writer.WriteAsync(
            new StateWriteRequest<HistoricalSettingsV2.Fragment>(
                new HistoricalSettingsV2.Fragment
                {
                    RetryCount = Optional<int>.Present(8),
                    NullableLabel = Optional<string?>.Present("from-v2"),
                    NewName = Optional<string?>.Present("name-v2"),
                }
            )
        );
        var dispatcher = HistoricalSettings.CreateSchemaDispatcher(
            new JsonStateCodec<HistoricalSettingsV1.Fragment>(),
            oldCodec
        );
        var source = new StateSource<HistoricalSettings.Fragment>("legacy-v2", new SerializedSource<HistoricalSettings.Fragment>(resource, new JsonStateCodec<HistoricalSettings.Fragment>(), schemaDispatcher: dispatcher, writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<HistoricalSettings.Fragment>());

        var result = await source.Reader.ReadAsync();

        (result.Status).ShouldBe(StateReadStatus.Success);
        (result.Schema).ShouldBe(HistoricalSettings.ConfiglueSchema.ToMetadata());
        (result.Value!.RetryCount.Value).ShouldBe(8);
        (result.Value.NullableLabel.Value).ShouldBe("from-v2");
        (result.Value.NewName.Value).ShouldBe("name-v2");
    }

    [Test]
    public async Task YamlCodec_DispatchesHistoricalGeneratedFragment()
    {
        var (_, model) = await ReadHistoricalSettingsAsync(
            new YamlStateCodec<HistoricalSettingsV1.Fragment>(
                modelSchema: HistoricalSettingsV1.FragmentSchema
            ),
            new YamlStateCodec<HistoricalSettings.Fragment>(
                modelSchema: HistoricalSettings.FragmentSchema
            )
        );

        (model.RetryCount).ShouldBe(0);
        (model.NullableLabel).ShouldBeNull();
        (model.NewName).ShouldBe("renamed");
    }

    [Test]
    public async Task SerializedSource_RejectsUnknownHistoricalVersionForConfiguredModel()
    {
        var futureSchema = new StateSchemaMetadata("historical-settings", 4);
        var resource = new InMemoryResource();
        var writer = new SerializedStateWriter<HistoricalSettingsV1.Fragment>(
            resource,
            new JsonStateCodec<HistoricalSettingsV1.Fragment>(),
            new StateCodecContext(futureSchema)
        );
        await writer.WriteAsync(new StateWriteRequest<HistoricalSettingsV1.Fragment>(new()));
        var dispatcher = HistoricalSettings.CreateSchemaDispatcher(
            new JsonStateCodec<HistoricalSettingsV1.Fragment>(),
            new JsonStateCodec<HistoricalSettingsV2.Fragment>()
        );
        var source = new StateSource<HistoricalSettings.Fragment>("legacy", new SerializedSource<HistoricalSettings.Fragment>(resource, new JsonStateCodec<HistoricalSettings.Fragment>(), schemaDispatcher: dispatcher, writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<HistoricalSettings.Fragment>());

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await source.Reader.ReadAsync()
        );
    }

    private static async Task<(
        HistoricalSettings.Fragment Fragment,
        HistoricalSettings Model
    )> ReadHistoricalSettingsAsync<TLegacyCodec, TCurrentCodec>(
        TLegacyCodec oldCodec,
        TCurrentCodec currentCodec
    )
        where TLegacyCodec : IStateCodec<HistoricalSettingsV1.Fragment>
        where TCurrentCodec : IStateCodec<HistoricalSettings.Fragment>
    {
        var resource = new InMemoryResource();
        var oldWriter = new SerializedStateWriter<HistoricalSettingsV1.Fragment>(
            resource,
            oldCodec
        );
        await oldWriter.WriteAsync(
            new StateWriteRequest<HistoricalSettingsV1.Fragment>(
                new HistoricalSettingsV1.Fragment
                {
                    RetryCount = Optional<int>.Present(0),
                    NullableLabel = Optional<string?>.Present(null),
                    OldName = Optional<string?>.Present("renamed"),
                }
            )
        );

        var dispatcher = HistoricalSettings.CreateSchemaDispatcher(
            oldCodec,
            new JsonStateCodec<HistoricalSettingsV2.Fragment>(),
            static previous =>
            {
                var builder = HistoricalSettings.Fragment.FromPrevious(previous).ToBuilder();
                if (previous.OldName.IsPresent)
                {
                    builder.NewName = previous.OldName;
                }

                return builder.Build();
            }
        );
        var source = new StateSource<HistoricalSettings.Fragment>("legacy", new SerializedSource<HistoricalSettings.Fragment>(resource, currentCodec, schemaDispatcher: dispatcher, writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<HistoricalSettings.Fragment>());

        var fragmentResult = await source.Reader.ReadAsync();
        (fragmentResult.Status).ShouldBe(StateReadStatus.Success);
        (fragmentResult.Schema).ShouldBe(HistoricalSettings.ConfiglueSchema.ToMetadata());
        var options = new ConfiglueRuntime<HistoricalSettings, HistoricalSettings.Fragment>(
            new StateSourceSet<HistoricalSettings.Fragment>([source])
        );
        var modelResult = await options.ReadAsync();
        (modelResult.Status).ShouldBe(StateReadStatus.Success);
        (modelResult.Schema).ShouldBe(HistoricalSettings.ConfiglueSchema.ToMetadata());
        return (fragmentResult.Value!, modelResult.Value!);
    }
}
