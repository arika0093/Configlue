using System.Text.Json;
using Configlue.Provider.Json;

namespace Configlue.Tests;

public sealed class IssueReviewRegressionTests
{
    [Test]
    public async Task DefaultContextsPassThroughSourceResolverWriterWatcherCompositeAndFallback()
    {
        var backend = new ReviewSource();
        var source = new StateSource<ExplicitReviewSettings.Fragment>("review", backend, writer: backend, watcher: backend);
        var set = new StateSourceSet<ExplicitReviewSettings.Fragment>(new[] { source });
        var resolver = new StateSourceResolver<ExplicitReviewSettings.Fragment>(set);
        var writer = new StateSourceWriter<ExplicitReviewSettings.Fragment>(set);
        var watcher = new StateSourceWatcher<ExplicitReviewSettings.Fragment>(resolver);
        var composite = new CompositeStateSource<ExplicitReviewSettings.Fragment>(set, "review");
        var fallback = new FallbackStateSource<ExplicitReviewSettings.Fragment>(set);
        foreach (var context in new[] { default(ConfiglueResourceContext), ConfiglueResourceContext.Default })
        {
            await source.ReadAsync(context);
            await resolver.ReadAsync(context);
            await writer.WriteAsync(context, new StateWriteRequest<ExplicitReviewSettings.Fragment>(ExplicitReviewSettings.Fragment.Empty));
            await watcher.WaitForChangeAsync(context, null);
            await composite.ReadAsync(context);
            await composite.WaitForChangeAsync(context, null);
            await fallback.ReadAsync(context);
            await fallback.WriteAsync(context, new StateWriteRequest<ExplicitReviewSettings.Fragment>(ExplicitReviewSettings.Fragment.Empty));
            backend.LastContext.ShouldBe(ConfiglueResourceContext.Default);
            backend.LastContext.Subject.ShouldNotBeNull();
        }
        var invalid = new ConfiglueResourceContext { ModelId = "invalid" };
        Should.Throw<ArgumentException>(() => source.ReadAsync(invalid));
        Should.Throw<ArgumentException>(() => source.WriteAsync(invalid, new StateWriteRequest<ExplicitReviewSettings.Fragment>(ExplicitReviewSettings.Fragment.Empty)));
        Should.Throw<ArgumentException>(() => source.WaitForChangeAsync(invalid, null));
        Should.Throw<ArgumentException>(() => source.GetResourceId(invalid));
        var mutation = new ResourceWriteMutation(RevisionCondition.None, null, _ => ReadOnlyMemory<byte>.Empty);
        mutation.Context.ShouldBe(ConfiglueResourceContext.Default);
        mutation.WithContext(default).Context.ShouldBe(ConfiglueResourceContext.Default);
        Should.Throw<ArgumentException>(() => mutation.WithContext(invalid));
    }

    private sealed class ReviewSource : ISourceReader<ExplicitReviewSettings.Fragment>, ISourceWriter<ExplicitReviewSettings.Fragment>, ISourceWatcher
    {
        public ConfiglueResourceContext LastContext { get; private set; }
        public ValueTask<StateReadResult<ExplicitReviewSettings.Fragment>> ReadAsync(ConfiglueResourceContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return ValueTaskCompat.FromResult(StateReadResult<ExplicitReviewSettings.Fragment>.Success(ExplicitReviewSettings.Fragment.Empty));
        }
        public ValueTask<StateWriteResult> WriteAsync(ConfiglueResourceContext context, StateWriteRequest<ExplicitReviewSettings.Fragment> request, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return ValueTaskCompat.FromResult(new StateWriteResult("written"));
        }
        public ValueTask WaitForChangeAsync(ConfiglueResourceContext context, string? observedRevision, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return default;
        }
    }

    [Test]
    public void ZeroContextIsCanonicalAndMalformedContextFails()
    {
        var zero = default(ConfiglueResourceContext);
        zero.ShouldBe(ConfiglueResourceContext.Default);
        zero.GetHashCode().ShouldBe(ConfiglueResourceContext.Default.GetHashCode());
        (ConfiglueResourceContext.Default with { ModelId = "model" }).Subject.ShouldBe(zero.Subject);
        zero.Subject.ShouldBe(ConfiglueResourceContext.Default.Subject);
        var request = new ResourceWriteRequest(new byte[] { 1 });
        ResourceWriteMutation.Replace(request, zero).Context.ShouldBe(zero);
        ResourceWriteMutation.Replace(request, ConfiglueResourceContext.Default).Context.ShouldBe(zero);
        Should.Throw<ArgumentException>(() => ResourceWriteMutation.Replace(request, new ConfiglueResourceContext { ModelId = "invalid" }));
        Should.Throw<ArgumentException>(() => _ = new ConfiglueResourceContext { ModelId = "invalid" }.Subject);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void JsonAmbiguityFailsForSparseWritesAndEmptyReads(bool namingPolicy)
    {
        var options = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = !namingPolicy,
            PropertyNamingPolicy = namingPolicy ? JsonNamingPolicy.CamelCase : null
        };
        options.Converters.Add(new CaseCollisionSettings.Fragment.FragmentJsonConverter());
        var sparse = new CaseCollisionSettings.Fragment { Name = Optional<int>.Present(1) };
        Should.Throw<JsonException>(() => JsonSerializer.Serialize(sparse, options));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<CaseCollisionSettings.Fragment>("{}", options));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<CaseCollisionSettings.Fragment>("{\"name\":1}", options));
    }

    [Test]
    public void EmptyGeneratedJsonFragmentReadsUnknownProperties()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new EmptyReviewSettings.Fragment.FragmentJsonConverter());
        JsonSerializer.Deserialize<EmptyReviewSettings.Fragment>("{\"unknown\":{\"nested\":[1,2]}}", options)!.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void ExplicitJsonNamesRoundTrip()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new ExplicitReviewSettings.Fragment.FragmentJsonConverter());
        var fragment = new ExplicitReviewSettings.Fragment { Value = Optional<int>.Present(42) };
        var json = JsonSerializer.Serialize(fragment, options);
        json.ShouldContain("custom_value");
        JsonSerializer.Deserialize<ExplicitReviewSettings.Fragment>(json, options)!.Value.Value.ShouldBe(42);
    }
}

[ConfiglueModel("review.case")]
internal partial class CaseCollisionSettings
{
    public int Name { get; set; }
    public int name { get; set; }
}

[ConfiglueModel("review.empty")]
internal partial class EmptyReviewSettings { }

[ConfiglueModel("review.explicit")]
internal partial class ExplicitReviewSettings
{
    [System.Text.Json.Serialization.JsonPropertyName("custom_value")]
    public int Value { get; set; }
}
