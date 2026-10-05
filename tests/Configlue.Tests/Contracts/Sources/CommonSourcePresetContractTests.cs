using System.Net;
using System.Net.Http.Headers;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Source.Http;
using Configlue.Source.Presets;

namespace Configlue.Tests;

public sealed class CommonSourcePresetContractTests
{
    [Test]
    public async Task WithHttpPolicy_ComposesReadOnlyHttpLayer()
    {
        const string endpoint = "http://localhost/api/settings";
        const string revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var body = ConfiglueFragmentJson.SerializeToCanonicalBytes(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(42),
                Label = Optional<string?>.Present("http-policy"),
            },
            typeof(AppSettings.Fragment),
            null
        );
        using var client = new HttpClient(new StubHandler(body, revision))
        {
            BaseAddress = new Uri("http://localhost"),
        };

        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.UseCommonSources(sources =>
            {
                sources.WithHttpPolicy(endpoint, client);
                sources.Add<AppSettings>();
            })
        );

        var state = context.GetRuntimeState<AppSettings>();
        var value = await state.GetValueAsync();
        value.RetryCount.ShouldBe(42);
        value.Label.ShouldBe("http-policy");

        var diagnostics = state.GetDiagnostics();
        var source = diagnostics.Sources.Single();
        source.Id.ShouldBe(SourceId.From("common.http"));
        source.PhysicalOrigin.ShouldBe(new Uri(endpoint).AbsoluteUri);
        source.Priority.ShouldBe(600);
        source.FallbackCondition.ShouldBe(StateFallbackCondition.NotFound);
        source.CanRead.ShouldBeTrue();
        source.CanWrite.ShouldBeFalse();
        diagnostics.DefaultWriteSourceId.ShouldBeNull();
        ((await state.GetDetailsAsync()).RetryCount.Source?.CanWrite == false).ShouldBeTrue();
    }

    [Test]
    public void DefaultWriteLayer_RequiresAConfiguredFile()
    {
        Should.Throw<InvalidOperationException>(() =>
            ConfiglueApp.CreateContext(builder =>
                builder.UseCommonSources(sources =>
                {
                    sources.DefaultWriteLayer(CommonSourceLayer.UserGlobal);
                    sources.WithEnvironment("CONFIGLUE_TEST");
                    sources.Add<AppSettings>();
                })
            )
        );
    }

    [Test]
    public void CommonSources_RequireAtLeastOneEnabledSource()
    {
        Should.Throw<InvalidOperationException>(() =>
            ConfiglueApp.CreateContext(builder => builder.UseCommonSources(_ => { }))
        );
    }

    private sealed class StubHandler(byte[] body, string revision) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            response.Headers.ETag = EntityTagHeaderValue.Parse($"\"{revision}\"");
            return Task.FromResult(response);
        }
    }
}
