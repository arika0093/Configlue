---
title: Amazon S3 object resource
description: Use an Amazon S3 object as an optional byte-oriented Configlue resource.
---

# Amazon S3 object resource

`Configlue.Resource.S3` adapts one S3 object to Configlue's byte-resource contracts. Install it only when the application uses S3:

```sh
dotnet add package Configlue.Resource.S3
```

The package depends on the AWS SDK for .NET S3 client and is not referenced by `Configlue.Core` or the `Configlue` meta-package. Configure the SDK client with the application's credentials, region, and endpoint.

Register an object as a typed state source and keep serialization in Configlue's codec:

```csharp
using Configlue.Provider.Json;
using Configlue.Resource.S3;

model.Sources(sources => sources
    .FromS3Object(new S3ObjectSourceOptions
    {
        BucketName = "app-config",
        Key = "production/settings.json",
        Client = s3Client,
        Codec = new JsonStateCodec<AppSettings.Fragment>(),
    })
    .Named("production-settings")
    .Writable());
```

The returned registration shares `Named`, `Priority`, `FallbackWhen`, `ReadOnly`, `Writable`, and `ExplicitOnly` with other providers. `Named` is useful when application code needs to select the source for routing or migration.

In dependency-injected applications, use `ClientFactory` to resolve the host-owned `IAmazonS3`; the client remains externally owned. `S3ObjectResource` can also be used directly where a resource is needed.

Reads return the object bytes and ETag. When revision checking is requested, writes use S3 conditional `If-Match` with the expected ETag, or `If-None-Match: *` when the expected object is missing. S3 precondition failures become `StateConflictException`. Unconditional writes remain unconditional. A missing object is reported as not found; authorization, bucket, network, and other S3 errors are surfaced.

ETags are opaque S3 revisions, not necessarily content hashes (for example, multipart uploads and encryption can affect their meaning). Conditional-write guarantees require an S3 implementation that supports these request headers. This provider does not implement a change watcher, cross-object transactions, or bucket management.
