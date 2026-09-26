---
title: HTTP resource protocol
description: HTTP endpoints, revisions, writes, and polling used by Configlue HTTP resources.
---

# HTTP resource protocol

The Configlue HTTP resource transports serialized resource bytes. It does not impose a codec or a model type; compose it with JSON, YAML, XML, or a custom state codec through SerializedStateSource.FromResource.

## Endpoints

The endpoint root is a base URI. The default paths are GET {root}/get and PUT {root}/update. HttpResourceOptions can change both relative paths. Endpoint paths must remain beneath the root.

The caller supplies an HttpClient. Configure credentials, default request headers, certificates, proxy settings, and timeout on that client. Configlue does not own or dispose it.

## Read

GET {root}/get returns the resource bytes with status 200. The ETag response header is the opaque revision used for conditional writes and efficient polling. If the response omits ETag, the resource still polls by comparing content fingerprints, but it cannot conditionally replace an existing resource.

The optional response headers Configlue-Schema-Id and Configlue-Schema-Version carry StateSchemaMetadata. If either header is present, Configlue-Schema-Version must contain an invariant positive integer. The schema identifier is optional.

Response handling:

- 404 returns NotFound.
- 408, 429, and 5xx return Unavailable.
- Network failures and client timeout return Unavailable.
- 401, 403, other 4xx, redirects that are not followed by HttpClient, and malformed responses surface as exceptions.
- 304 is used only by conditional polling requests.

## Write

PUT {root}/update sends the serialized bytes. The default content type is application/octet-stream; set HttpResourceOptions.ContentType to match the endpoint contract.

When a write has an expected revision, Configlue sends If-Match with that strong ETag. If a write explicitly checks for a missing resource, Configlue sends If-None-Match: *. The endpoint must return 412 Precondition Failed when the condition does not match. Configlue maps 412 and 409 Conflict to StateConflictException.

On success, the endpoint returns any 2xx response and should include the resulting ETag. The response body is ignored. Configlue-Schema-Id and Configlue-Schema-Version request headers carry optional schema metadata.

## Polling

HttpResourceReader implements IStateWatcher. It polls at HttpResourceOptions.PollingInterval, defaulting to five seconds. When an ETag was observed, polls send If-None-Match and treat 304 as unchanged. Without ETag, polls compare resource status and content fingerprints. Caller cancellation stops both the delay and active HTTP request.

The watcher is an invalidation signal: after it returns, the Configlue runtime reads the resource again.
