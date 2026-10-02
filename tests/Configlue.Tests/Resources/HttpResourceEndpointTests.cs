using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Configlue;
using Configlue.Hosting.AspNetCore;
using Configlue.Resource.Http;
using Configlue.Resources;
using Configlue.State;
using Configlue.Testing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
            new ResourceWriteRequest(
                firstContent,
                Condition: RevisionCondition.MustNotExist,
                Schema: schema
            )
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

        using var anyMatchingTag = new HttpRequestMessage(HttpMethod.Get, reader.GetUri);
        anyMatchingTag.Headers.TryAddWithoutValidation(
            "If-None-Match",
            $"\"stale\", {firstRead.Revision}"
        );
        using var unchangedForList = await httpClient.SendAsync(anyMatchingTag);
        unchangedForList.StatusCode.ShouldBe(HttpStatusCode.NotModified);

        var secondContent = Encoding.UTF8.GetBytes("{\"RetryCount\":6}");
        var secondWrite = await writer.WriteAsync(
            new ResourceWriteRequest(
                secondContent,
                Condition: RevisionCondition.FromRevision(firstRead.Revision),
                Schema: schema
            )
        );
        secondWrite.Revision.ShouldNotBe(firstRead.Revision);

        var unconditionalContent = Encoding.UTF8.GetBytes("{\"RetryCount\":8}");
        var unconditionalWrite = await writer.WriteAsync(
            new ResourceWriteRequest(
                unconditionalContent,
                Condition: RevisionCondition.None,
                Schema: schema
            )
        );
        unconditionalWrite.Revision.ShouldNotBe(secondWrite.Revision);

        await Should.ThrowAsync<StateConflictException>(async () =>
        {
            await writer.WriteAsync(
                new ResourceWriteRequest(
                    firstContent,
                    Condition: RevisionCondition.FromRevision(firstRead.Revision),
                    Schema: schema
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
    public async Task Endpoint_UsesTrustedResolvedContextForReadsAndWritesAcrossRoutes()
    {
        var japan = new ConfiglueResourceContext(
            "http-settings",
            new EndpointSubject(SubjectKey.From("japan")),
            SubjectKey.From("japan-resource"),
            RouteKey.From("asia")
        );
        var europe = new ConfiglueResourceContext(
            "http-settings",
            new EndpointSubject(SubjectKey.From("europe")),
            SubjectKey.From("europe-resource"),
            RouteKey.From("eu")
        );
        var resource = new ContextAwareResource();
        resource.Set(japan, Encoding.UTF8.GetBytes("japan-before"));
        resource.Set(europe, Encoding.UTF8.GetBytes("europe-before"));
        var trustedContexts = new Dictionary<string, ConfiglueResourceContext>(StringComparer.Ordinal)
        {
            ["japan"] = japan,
            ["europe"] = europe,
        };
        await using var app = await StartAppAsync(endpoints =>
            endpoints.MapConfiglueHttpResource(
                "/config/{region}",
                resource,
                resource,
                new HttpResourceEndpointOptions
                {
                    ResourceContextResolver = (httpContext, cancellationToken) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var authenticatedSubject = httpContext.User.FindFirst("sub")?.Value;
                        return ValueTask.FromResult(trustedContexts[authenticatedSubject!]);
                    },
                }
            ),
            configureApplication: application =>
                application.Use(async (httpContext, next) =>
                {
                    var region = httpContext.Request.RouteValues["region"]?.ToString();
                    var subject = region switch
                    {
                        "jp" => "japan",
                        "eu" => "europe",
                        _ => throw new InvalidOperationException("The route is not authorized."),
                    };
                    httpContext.User = new ClaimsPrincipal(
                        new ClaimsIdentity([new Claim("sub", subject)], "trusted-test")
                    );
                    await next();
                })
        );
        using var httpClient = app.GetTestClient();

        using var japanRead = await httpClient.GetAsync("http://localhost/config/jp/get");
        (await japanRead.Content.ReadAsStringAsync()).ShouldBe("japan-before");
        resource.ReadContexts.Last().ShouldBe(japan);
        using var europeRead = await httpClient.GetAsync("http://localhost/config/eu/get");
        (await europeRead.Content.ReadAsStringAsync()).ShouldBe("europe-before");
        resource.ReadContexts.Last().ShouldBe(europe);

        using var update = new HttpRequestMessage(HttpMethod.Put, "http://localhost/config/jp/update")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("japan-after")),
        };
        update.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        update.Headers.TryAddWithoutValidation("X-Configlue-Model", "attacker-model");
        update.Headers.TryAddWithoutValidation("X-Configlue-Key", "attacker-key");
        update.Headers.TryAddWithoutValidation("X-Configlue-Route", "attacker-route");
        using var updated = await httpClient.SendAsync(update);

        updated.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        resource.WriteContexts.Last().ShouldBe(japan);
        Encoding.UTF8.GetString(resource.Get(japan)).ShouldBe("japan-after");
        Encoding.UTF8.GetString(resource.Get(europe)).ShouldBe("europe-before");
    }

    [Test]
    public async Task Endpoint_UsesDefaultContextWhenResolverIsNotConfigured()
    {
        var resource = new ContextAwareResource();
        await using var app = await StartAppAsync(endpoints =>
            endpoints.MapConfiglueHttpResource("/config", resource, resource)
        );
        using var httpClient = app.GetTestClient();

        using var response = await httpClient.GetAsync("http://localhost/config/get");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        resource.ReadContexts.Last().ShouldBe(ConfiglueResourceContext.Default);

        using var update = CreateWriteRequest(new Uri("http://localhost/config/update"));
        using var updated = await httpClient.SendAsync(update);
        updated.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        resource.WriteContexts.Last().ShouldBe(ConfiglueResourceContext.Default);
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

    [Test]
    public async Task Endpoint_RequiresAuthorizationByDefault()
    {
        var resource = new InMemoryResource();
        await using var app = await StartAppAsync(
            endpoints => endpoints.MapConfiglueHttpResource("/config", resource),
            allowAuthorization: false
        );
        using var httpClient = app.GetTestClient();

        using var response = await httpClient.GetAsync("http://localhost/config/get");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Endpoint_RejectsOversizedRequestBodies()
    {
        var resource = new InMemoryResource();
        await using var app = await StartAppAsync(endpoints =>
            endpoints.MapConfiglueHttpResource(
                "/config",
                resource,
                resource,
                new HttpResourceEndpointOptions { MaximumRequestBodySize = 1 }
            )
        );
        using var httpClient = app.GetTestClient();
        using var request = CreateWriteRequest(new Uri("http://localhost/config/update"));

        using var response = await httpClient.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        resource.WriteCount.ShouldBe(0);
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
        Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder> mapEndpoints,
        bool allowAuthorization = true,
        Action<WebApplication>? configureApplication = null
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder
            .Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAssertion(_ => allowAuthorization)
                .Build()
        );
        var app = builder.Build();
        configureApplication?.Invoke(app);
        mapEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class FixedResourceReader(ResourceReadResult result) : IResourceReader
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed record EndpointSubject(SubjectKey Key) : IConfiglueSubject;

    private sealed class ContextAwareResource : IResourceReader, IResourceWriter
    {
        private readonly ConcurrentDictionary<(string? ModelId, SubjectKey Key, RouteKey Route), byte[]> _states = new();
        private long _revision;

        public ConcurrentQueue<ConfiglueResourceContext> ReadContexts { get; } = new();
        public ConcurrentQueue<ConfiglueResourceContext> WriteContexts { get; } = new();

        public void Set(ConfiglueResourceContext context, byte[] content) =>
            _states[(context.ModelId, context.Key, context.Route)] = content;

        public byte[] Get(ConfiglueResourceContext context) =>
            _states[(context.ModelId, context.Key, context.Route)];

        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadContexts.Enqueue(context);
            return ValueTask.FromResult(
                _states.TryGetValue((context.ModelId, context.Key, context.Route), out var content)
                    ? ResourceReadResult.Success(content, $"revision:{Interlocked.Read(ref _revision)}")
                    : ResourceReadResult.NotFound()
            );
        }

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            ResourceWriteRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteContexts.Enqueue(context);
            _states[(context.ModelId, context.Key, context.Route)] = request.Content.ToArray();
            return ValueTask.FromResult(new StateWriteResult($"revision:{Interlocked.Increment(ref _revision)}"));
        }
    }

    private sealed class CancellableResourceReader : IResourceReader
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
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
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Resource access failed.");
        }
    }
}
