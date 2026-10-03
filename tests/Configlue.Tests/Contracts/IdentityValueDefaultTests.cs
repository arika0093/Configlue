using System.Text.Json;
using Configlue.Codecs;
using Configlue.Migrations;
using Configlue.Resources;

namespace Configlue.Tests;

public sealed class IdentityValueDefaultTests
{
    [Test]
    public void ResourceKeyIsDistinctFromSubjectKeyAndHasAnExplicitDefault()
    {
        ResourceKey uninitialized = default;
        ResourceKey subjectResourceKey = ResourceKey.From(SubjectKey.From("tenant-a"));
        ResourceKey providerResourceKey = ResourceKey.From("record:7");

        uninitialized.ShouldBe(ResourceKey.Default);
        uninitialized.IsDefault.ShouldBeTrue();
        uninitialized.Value.ShouldBe(string.Empty);
        subjectResourceKey.Value.ShouldBe(SubjectKey.From("tenant-a").Value);
        subjectResourceKey.ShouldNotBe(providerResourceKey);
        providerResourceKey.IsDefault.ShouldBeFalse();
    }

    [Test]
    public void DefaultResourceIdIsSafeButInvalidAndDistinctFromNullableAbsence()
    {
        ResourceId uninitialized = default;
        ResourceId? absent = null;
        var present = (ResourceId?)default(ResourceId);
        var resolved = new ResourceId("disk:settings");

        uninitialized.IsDefault.ShouldBeTrue();
        uninitialized.Value.ShouldBe(string.Empty);
        uninitialized.ToString().ShouldBe(string.Empty);
        uninitialized.ShouldBe(default(ResourceId));
        uninitialized.ShouldNotBe(resolved);
        resolved.IsDefault.ShouldBeFalse();
        resolved.Value.ShouldBe("disk:settings");
        absent.ShouldBeNull();
        present.HasValue.ShouldBeTrue();
        present.Value.IsDefault.ShouldBeTrue();

        ResourceId roundTrip = JsonSerializer.Deserialize<ResourceId>(
            JsonSerializer.Serialize(resolved)
        );
        roundTrip.ShouldBe(resolved);
        roundTrip.IsDefault.ShouldBeFalse();
        ResourceId serializedDefault = JsonSerializer.Deserialize<ResourceId>(
            JsonSerializer.Serialize(uninitialized)
        );
        serializedDefault.IsDefault.ShouldBeTrue();
        serializedDefault.Value.ShouldBe(string.Empty);
    }

    [Test]
    public void ContextIdentityResolutionRejectsDefaultRequiredIdsAndAllowsTryAbsence()
    {
        var context = ConfiglueResourceContext.Default;

        Should.Throw<InvalidOperationException>(() =>
            ResourceContextExtensions.GetResourceId(new RequiredIdentity(default), context)
        );
        Should.Throw<InvalidOperationException>(() =>
            ResourceContextExtensions.TryGetResourceId(
                new RequiredIdentity(default),
                context,
                out _
            )
        );
        Should.Throw<InvalidOperationException>(() =>
            ResourceContextExtensions.TryGetResourceId(
                new OptionalIdentity(true, default),
                context,
                out _
            )
        );
        ResourceContextExtensions
            .TryGetResourceId(new OptionalIdentity(false, default), context, out var absentId)
            .ShouldBeFalse();
        absentId.ShouldBe(default(ResourceId));

        var expected = new ResourceId("memory:test");
        ResourceContextExtensions
            .GetResourceId(new RequiredIdentity(expected), context)
            .ShouldBe(expected);
        ResourceContextExtensions
            .TryGetResourceId(new OptionalIdentity(true, expected), context, out var actual)
            .ShouldBeTrue();
        actual.ShouldBe(expected);
    }

    [Test]
    public void DefaultSourceKeyHasSafeNameAndIsRejectedWhenRequired()
    {
        SourceKey<AppSettings> uninitialized = default;
        var named = SourceKey<AppSettings>.Named("primary");

        uninitialized.IsDefault.ShouldBeTrue();
        uninitialized.Name.ShouldBe(string.Empty);
        uninitialized.ShouldBe(default(SourceKey<AppSettings>));
        uninitialized.ShouldNotBe(named);
        named.IsDefault.ShouldBeFalse();
        named.Name.ShouldBe("primary");

        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(uninitialized));
        serialized.RootElement.GetProperty("Name").GetString().ShouldBe(string.Empty);
        serialized.RootElement.GetProperty("IsDefault").GetBoolean().ShouldBeTrue();

        Should.Throw<ArgumentException>(() =>
            StateWritePlan.For<AppSettings>().DefaultTo(uninitialized)
        );
    }

    [Test]
    public void DefaultSourceIdIsSafeInvalidAndDistinctFromNullableAbsence()
    {
        SourceId uninitialized = default;
        SourceId? absent = null;
        var presentDefault = (SourceId?)default(SourceId);
        var named = SourceId.From("primary");

        uninitialized.IsDefault.ShouldBeTrue();
        uninitialized.Value.ShouldBe(string.Empty);
        uninitialized.ToString().ShouldBe(string.Empty);
        uninitialized.ShouldBe(default(SourceId));
        uninitialized.ShouldNotBe(named);
        absent.ShouldBeNull();
        presentDefault.HasValue.ShouldBeTrue();
        presentDefault.Value.IsDefault.ShouldBeTrue();
        JsonSerializer.Deserialize<SourceId>(JsonSerializer.Serialize(named)).ShouldBe(named);
        JsonSerializer
            .Deserialize<SourceId>(JsonSerializer.Serialize(uninitialized))
            .ShouldBe(uninitialized);

        Should.Throw<ArgumentException>(() => StateWritePlan.DefaultTo(uninitialized));
    }

    [Test]
    public void DefaultGeneratedMemberTokensExposeSafeNamesAndCannotEnterModelSchemas()
    {
        ConfiglueMemberSchema schemaMember = default;
        ConfiglueFragmentMember fragmentMember = default;

        schemaMember.IsDefault.ShouldBeTrue();
        schemaMember.Name.ShouldBe(string.Empty);
        schemaMember.ValueType.ShouldBe(typeof(void));
        schemaMember.ShouldBe(default(ConfiglueMemberSchema));
        fragmentMember.IsDefault.ShouldBeTrue();
        fragmentMember.Name.ShouldBe(string.Empty);
        fragmentMember.ShouldBe(default(ConfiglueFragmentMember));
        var serializedFragmentMember = JsonSerializer.Deserialize<ConfiglueFragmentMember>(
            JsonSerializer.Serialize(fragmentMember)
        );
        serializedFragmentMember.IsDefault.ShouldBeTrue();
        serializedFragmentMember.Name.ShouldBe(string.Empty);

        Should.Throw<ArgumentException>(() =>
            new ConfiglueModelSchema(typeof(AppSettings), "app-settings", 1, [schemaMember])
        );
    }

    [Test]
    public void DefaultSchemaIsInvalidDistinctFromNullableAbsenceAndCannotEnterTypedConsumers()
    {
        StateSchemaMetadata uninitialized = default;
        StateSchemaMetadata? absent = null;
        var valid = new StateSchemaMetadata("app-settings", 2);

        typeof(StateSchemaMetadata).GetProperty(nameof(StateSchemaMetadata.ModelId))!
            .SetMethod.ShouldBeNull();
        typeof(StateSchemaMetadata).GetProperty(nameof(StateSchemaMetadata.Version))!
            .SetMethod.ShouldBeNull();

        uninitialized.IsDefault.ShouldBeTrue();
        uninitialized.IsValid.ShouldBeFalse();
        uninitialized.Version.ShouldBe(0);
        uninitialized.ShouldBe(default(StateSchemaMetadata));
        uninitialized.ShouldNotBe(valid);
        absent.ShouldBeNull();
        new StateCodecContext(absent).Schema.ShouldBeNull();
        new StateCodecContext(valid).Schema.ShouldBe(valid);
        StateSchemaMetadata roundTrip = JsonSerializer.Deserialize<StateSchemaMetadata>(
            JsonSerializer.Serialize(valid)
        );
        roundTrip.ShouldBe(valid);
        roundTrip.IsValid.ShouldBeTrue();

        Should.Throw<ArgumentException>(() => new StateCodecContext(uninitialized));
        Should.Throw<ArgumentException>(() =>
            StateReadResult<AppSettings>.Success(new AppSettings(), schema: uninitialized)
        );
        Should.Throw<ArgumentException>(() =>
            ResourceReadResult.Success(ReadOnlyMemory<byte>.Empty, schema: uninitialized)
        );
        Should.Throw<ArgumentException>(() =>
            new ResourceWriteMutation(
                RevisionCondition.None,
                uninitialized,
                static _ => ReadOnlyMemory<byte>.Empty
            )
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new StateSchemaDispatcher<AppSettings.Fragment>(uninitialized)
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new StateSchemaMigrationChain<AppSettings.Fragment>(uninitialized)
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            StateSchemaReference.CreateUri("schemas", uninitialized)
        );
        StateSchemaMetadata serializedDefault = JsonSerializer.Deserialize<StateSchemaMetadata>(
            JsonSerializer.Serialize(uninitialized)
        );
        serializedDefault.IsDefault.ShouldBeTrue();
        serializedDefault.IsValid.ShouldBeFalse();
    }

    private sealed class RequiredIdentity(ResourceId resourceId) : IResourceIdentity
    {
        public ResourceId GetResourceId(ConfiglueResourceContext context) => resourceId;
    }

    private sealed class OptionalIdentity(bool hasIdentity, ResourceId resourceId)
        : ITryResourceIdentity
    {
        public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId result)
        {
            result = resourceId;
            return hasIdentity;
        }
    }
}
