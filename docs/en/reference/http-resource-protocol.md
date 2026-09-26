---
title: HTTP resource protocol
description: Serve Configlue byte resources over HTTP and use them as state sources.
---

# HTTP resource protocol

`Configlue.Resource.Http` provides an HTTP reader and an opt-in writer. The optional `Configlue.Resource.Http.AspNetCore` package maps the same byte protocol to ASP.NET Core Minimal APIs. Both packages leave serialization to the application's state codec.

## ASP.NET Core endpoint

Install `Configlue.Resource.Http.AspNetCore` in an ASP.NET Core application and pass it an `IResourceReader`. Pass an `IResourceWriter` only when the resource may be changed through HTTP:

```csharp
using Configlue;
using Configlue.Resource.Http.AspNetCore;

var app = WebApplication.CreateBuilder(args).Build();

IResourceReader reader = GetResourceReader();
IResourceWriter writer = GetResourceWriter();

app.MapConfiglueHttpResource("/api/settings", reader, writer)
    .RequireAuthorization();

await app.RunAsync();
```

The default routes are `GET /api/settings/get` and `PUT /api/settings/update`. If no writer is supplied, the update route is not mapped. The returned route group can be used for endpoint conventions such as authorization.

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
    });
```

The paths must be relative, must not include a query or fragment, and must stay under the configured route root. Writes must use the configured media type. The default is `application/octet-stream`.

## Wire behavior

| Request or resource result | Response or behavior |
| --- | --- |
| `GET` returns `Success` | `200`, resource bytes, `ETag`, and schema headers when present |
| `GET` returns `NotFound` | `404` |
| `GET` returns `Unavailable` | `503` |
| `GET` has a matching `If-None-Match` | `304`; `If-None-Match: *` matches an existing resource |
| `PUT` has `If-Match: <etag>` | Writes with the decoded expected backend revision and checks it |
| `PUT` has `If-None-Match: *` | Writes only if the resource has no current revision |
| `PUT` has no condition | Writes without a revision check |
| Write succeeds | `204` and the new `ETag` when the writer returns a revision |
| Writer reports `StateConflictException` | `412` |
| Malformed condition or schema headers | `400` |
| Missing or mismatched write content type | `415` |

The endpoint encodes each non-null backend revision as a strong ETag with the form `"cfg1.<base64url>"`. This preserves opaque revisions such as a hexadecimal hash without assuming that they are valid HTTP entity tags. The HTTP client returns the ETag string as the resource revision and sends it back in `If-Match` or `If-None-Match`.

Conditional headers accept one ETag at a time. Reads accept one `If-None-Match` tag or `*`; writes accept one strong Configlue ETag in `If-Match`, or `If-None-Match: *` for a create check. A write cannot include both conditions. Tag lists, weak write tags, and unrecognized write tags receive `400`.

Schema metadata uses `Configlue-Schema-Id` and `Configlue-Schema-Version`. A version must be a positive integer; the model ID is optional. The endpoint passes the parsed metadata to the writer and emits it on successful reads.

The endpoint passes ASP.NET Core's `RequestAborted` token to the resource handler. Other handler exceptions are left to the application's ASP.NET Core error handling.

## HTTP client

Use the endpoint root (including its trailing path prefix) to create a reader. The paths and media type must match the server options:

```csharp
using Configlue.Resource.Http;

var reader = new HttpResourceReader(
    httpClient,
    new Uri("https://config.example/api/settings/"));

ResourceReadResult result = await reader.ReadAsync();
HttpResourceWriter writer = reader.CreateWriter();
```

When the server uses custom paths, configure the same paths on the client:

```csharp
using Configlue.Resource.Http;

var reader = new HttpResourceReader(
    httpClient,
    new Uri("https://config.example/api/settings/"),
    new HttpResourceOptions
    {
        GetPath = "state/read",
        UpdatePath = "state/write",
    });
```

Creating a writer does not grant server-side access; only map the update route when writes are intended. For a read-only source, compose the reader with `SerializedStateSource.FromResource` and a codec. For a writable source, pass `writer: reader.CreateWriter()` only when the remote endpoint supports updates.

The client maps `404` to `NotFound`, and `408`, `429`, `5xx`, transport failures, and request timeouts to `Unavailable`. Other unsuccessful responses are surfaced as `HttpRequestException`. Conditional write failures (`409` or `412`) are surfaced as `StateConflictException`.
