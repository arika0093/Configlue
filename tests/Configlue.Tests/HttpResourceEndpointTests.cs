using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Configlue;
using Configlue.Resource.Http;
using Configlue.Resource.Http.AspNetCore;
using Configlue.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Configlue.Tests;

public sealed class HttpResourceEndpointTests
{
    [Test]
    public async Task Endpoint_RoundTripsOpaqueRevisionSchemaAndConditionalWrites()
    {
        var resource = new InMemoryResource();
        await using var app = await StartAppAsync(
            (endpoints) => endpoints.MapConfiglueHttpResource("/config", resource, resource)
        );
        using var httpClient = app.GetTestClient();
        var reader = new HttpResourceReader(httpClient, new Uri("http://localhost/config/"));
        var writer = reader.CreateWriter();

        (await reader.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);

        var schema = new StateSchemaMetadata("AppSettings", 3);
        var firstContent = Encoding.UTF8.GetBytes("{\"RetryCount\":5}");
        var firstWrite = await writer.WriteAsync(
            new ResourceWriteRequest(firstContent, Schema: schema, CheckRevision: true)
        );

        firstWrite.Revision.ShouldNotBeNull();
        firstWrite.Revision.ShouldStartWith("\"cfg1.");
        var firstRead = await reader.ReadAsync();
        firstRead.Status.ShouldBe(StateReadStatus.Success);
        firstRead.Content.ToArray().ShouldBe(firstContent);
        firstRead.Revision.ShouldBe(firstWrite.Revision);
        firstRead.Schema.ShouldBe(schema);

        using var conditionalGet = new HttpRequestMessage(HttpMethod.Get, reader.GetUri);
        conditionalGet.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(firstRead.Revision!));
        using var unchanged = await httpClient.SendAsync(conditionalGet);
        unchanged.StatusCode.ShouldBe(HttpStatusCode.NotModified);

        using var anyExisting = new HttpRequestMessage(HttpMethod.Get, reader.GetUri);
        anyExisting.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var unchangedForWildcard = await httpClient.SendAsync(anyExisting);
        unchangedForWildcard.StatusCode.ShouldBe(HttpStatusCode.NotModified);

        var secondContent = Encoding.UTF8.GetBytes("{\"RetryCount\":6}");
        var secondWrite = await writer.WriteAsync(
            new ResourceWriteRequest(
                secondContent,
                ExpectedRevision: firstRead.Revision,
                Schema: schema,
                CheckRevision: true
            )
        );
        secondWrite.Revision.ShouldNotBe(firstRead.Revision);

        var unconditionalContent = Encoding.UTF8.GetBytes("{\"RetryCount\":8}");
        var unconditionalWrite = await writer.WriteAsync(
            new ResourceWriteRequest(unconditionalContent, Schema: schema)
        );
        unconditionalWrite.Revision.ShouldNotBe(secondWrite.Revision);

        await Should.ThrowAsync<StateConflictException>(async () =>
        {
            await writer.WriteAsync(
                new ResourceWriteRequest(
                    firstContent,
                    ExpectedRevision: firstRead.Revision,
                    Schema: schema,
                    CheckRevision: true
                )
            );
        });

        (await reader.ReadAsync()).Content.ToArray().ShouldBe(unconditionalContent);
    }

    [Test]
    public async Task Endpoint_MapsCustomRelativePathsAndOmitsUpdateWithoutAWriter()
    {
        var resource = new InMemoryResource();
        await using var app = await StartAppAsync(endpoints =>
            endpoints.MapConfiglueHttpResource(
                "/config",
                resource,
                options: new HttpResourceEndpointOptions
                {
                    GetPath = "state/read",
                    UpdatePath = "state/write",
                }
            )
        );
        using var httpClient = app.GetTestClient();
        var reader = new HttpResourceReader(
            httpClient,
            new Uri("http://localhost/config/"),
            new HttpResourceOptions { GetPath = "state/read", UpdatePath = "state/write" }
        );

        (await reader.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);
        using var update = await httpClient.PutAsync(
            new Uri("http://localhost/config/state/write"),
            new ByteArrayContent(Encoding.UTF8.GetBytes("unused"))
        );
        update.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        resource.WriteCount.ShouldBe(0);
    }

    [Test]
    public async Task Endpoint_MapsUnavailableAndRejectsMalformedWriteHeaders()
    {
        var reader = new FixedResourceReader(ResourceReadResult.Unavailable());
        await using var app = await StartAppAsync(endpoints =>
            endpoints.MapConfiglueHttpResource("/config", reader, new InMemoryResource())
        );
        using var httpClient = app.GetTestClient();
        var root = new Uri("http://localhost/config/");

        using var unavailable = await httpClient.GetAsync(new Uri(root, "get"));
        unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        using var malformedCondition = CreateWriteRequest(new Uri(root, "update"));
        malformedCondition.Headers.TryAddWithoutValidation("If-Match", "\"plain-revision\"");
        using var badConditionResponse = await httpClient.SendAsync(malformedCondition);
        badConditionResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var duplicateConditions = CreateWriteRequest(new Uri(root, "update"));
        duplicateConditions.Headers.TryAddWithoutValidation("If-Match", "\"cfg1.YQ\", \"cfg1.Yg\"");
        using var duplicateConditionResponse = await httpClient.SendAsync(duplicateConditions);
        duplicateConditionResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var malformedSchema = CreateWriteRequest(new Uri(root, "update"));
        malformedSchema.Headers.TryAddWithoutValidation(
            HttpResourceReader.SchemaVersionHeaderName,
            "0"
        );
        using var badSchemaResponse = await httpClient.SendAsync(malformedSchema);
        badSchemaResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var wrongContentType = new HttpRequestMessage(HttpMethod.Put, new Uri(root, "update"))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        using var unsupported = await httpClient.SendAsync(wrongContentType);
        unsupported.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Test]
    public async Task Endpoint_PassesRequestCancellationToReader()
    {
        var reader = new CancellableResourceReader();
        await using var app = await StartAppAsync(endpoints =>
            endpoints.MapConfiglueHttpResource("/config", reader)
        );
        using var httpClient = app.GetTestClient();
        using var cancellation = new CancellationTokenSource();
        var pendingRequest = httpClient.GetAsync("http://localhost/config/get", cancellation.Token);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await pendingRequest;
        });
        await reader.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Endpoint_LeavesOtherResourceExceptionsToApplicationHandling()
    {
        await using var app = await StartAppAsync(endpoints =>
            endpoints.MapConfiglueHttpResource("/config", new ThrowingResourceReader())
        );
        using var httpClient = app.GetTestClient();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await httpClient.GetAsync("http://localhost/config/get");
        });
    }

    [Test]
    public async Task Endpoint_RejectsPathsThatEscapeTheRouteRoot()
    {
        var resource = new InMemoryResource();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        Should.Throw<ArgumentException>(() =>
            app.MapConfiglueHttpResource(
                "/config",
                resource,
                options: new HttpResourceEndpointOptions { GetPath = "../outside" }
            )
        );
    }

    private static HttpRequestMessage CreateWriteRequest(Uri endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    private static async Task<WebApplication> StartAppAsync(
        Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder> mapEndpoints
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        mapEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private sealed class FixedResourceReader(ResourceReadResult result) : IResourceReader
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancellableResourceReader : IResourceReader
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ResourceReadResult> ReadAsync(
            CancellationToken cancellationToken = default
        )
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("The infinite wait completed unexpectedly.");
        }
    }

    private sealed class ThrowingResourceReader : IResourceReader
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Resource access failed.");
        }
    }
}
