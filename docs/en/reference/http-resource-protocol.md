---
title: HTTP resource protocol
description: Serve Configlue byte resources over HTTP and use them as state sources.
---

# HTTP resource protocol

`Configlue.Resource.Http` provides an HTTP reader and an opt-in writer. The optional `Configlue.Resource.Http.AspNetCore` package maps the same byte protocol to ASP.NET Core Minimal APIs. Both packages leave serialization to the application's state codec.

## ASP.NET Core endpoint

Install `Configlue.Resource.Http.AspNetCore` in an ASP.NET Core application and pass it an `IResourceReader`. Pass an `IResourceWriter` only when the resource may be changed through HTTP:

```csharp
using Configlue.Resources;
using Configlue.State;
using Configlue.Resource.Http.AspNetCore;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication("Configlue").AddCookie("Configlue");
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

IResourceReader reader = GetResourceReader();
IResourceWriter writer = GetResourceWriter();

app.MapConfiglueHttpResource("/api/settings", reader, writer);

await app.RunAsync();
```

The default routes are `GET /api/settings/get` and `PUT /api/settings/update`. If no writer is supplied, the update route is not mapped. Endpoints require authorization by default; configure authentication and authorization in the host. Set `RequireAuthorization = false` only when the endpoint is intentionally public. PUT request bodies are limited to 30,000,000 bytes by default; `MaximumRequestBodySize` changes the limit, and `null` disables it.

`HttpResourceEndpointOptions` configures paths relative to the root and the payload media type:

```csharp
app.MapConfiglueHttpResource(
    "/api/settings",
    reader,
    writer,
    new HttpResourceEndpointOptions
    {
        GetPath = "state/read",
        UpdatePath = "state/write",
        ContentType = "application/octet-stream",
        MaximumRequestBodySize = 10_000_000,
    });
```

The paths must be relative and stay under the configured route root. Writes must use the configured media type. The default is `application/octet-stream`.

## HTTP client

Use the endpoint root (including its trailing path prefix) to create a reader. The paths and media type must match the server options:

```csharp
using Configlue.Resource.Http;

var reader = new HttpResourceReader(
    httpClient,
    new Uri("https://config.example/api/settings/"));
HttpResourceWriter writer = reader.CreateWriter();
```

Creating a writer does not grant server-side access; only map the update route when writes are intended. For a read-only source, compose the reader with `SerializedStateSource.FromResource` and a codec. For a writable source, pass `writer: reader.CreateWriter()` only when the remote endpoint supports updates.

The client polls for changes (every 5 seconds by default) and surfaces conditional-write failures as `StateConflictException`. Failed polls use exponential backoff up to 30 seconds by default. Each HTTP request has a 30-second timeout by default; configure `PollingInterval`, `MaximumPollingInterval`, and `RequestTimeout` on `HttpResourceOptions` as needed. Missing resources map to `NotFound` fall-through; transport failures and timeouts map to `Unavailable`. Schema metadata travels in the `Configlue-Schema-Id` and `Configlue-Schema-Version` headers.

For registration options (`Client` vs `ClientFactory`, `Writable`, `WatchChanges`, `FallbackCondition`), see [HTTP and ZIP](../sources/http-and-zip.md).
