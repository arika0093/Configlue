using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Configlue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Configlue.Resource.Http.AspNetCore;

/// <summary>Maps the Configlue byte-resource HTTP protocol to ASP.NET Core endpoints.</summary>
public static class HttpResourceEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>GET {routeRoot}/{getPath}</c> and, when a writer is supplied,
    /// <c>PUT {routeRoot}/{updatePath}</c> endpoints.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="routeRoot">An absolute route prefix such as <c>/config/app</c>.</param>
    /// <param name="reader">The resource implementation used to read bytes and metadata.</param>
    /// <param name="writer">The optional resource implementation used to write bytes.</param>
    /// <param name="options">The relative paths and payload media type.</param>
    /// <returns>The route group containing the mapped endpoints.</returns>
    public static RouteGroupBuilder MapConfiglueHttpResource(
        this IEndpointRouteBuilder endpoints,
        string routeRoot,
        IResourceReader reader,
        IResourceWriter? writer = null,
        HttpResourceEndpointOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeRoot);

        var validatedOptions = ValidateOptions(options ?? new HttpResourceEndpointOptions());
        ValidateRouteRoot(routeRoot);

        var group = endpoints.MapGroup(routeRoot);
        if (validatedOptions.RequireAuthorization)
        {
            group.RequireAuthorization();
        }

        group.MapGet(
            validatedOptions.GetPath,
            context => HandleReadAsync(reader, validatedOptions, context)
        );
        if (writer is not null)
        {
            group.MapPut(
                validatedOptions.UpdatePath,
                context => HandleWriteAsync(writer, validatedOptions, context)
            );
        }

        return group;
    }

    private static async Task HandleReadAsync(
        IResourceReader reader,
        ValidatedOptions options,
        HttpContext context
    )
    {
        if (!TryReadIfNoneMatch(context.Request, out var condition))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var result = await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false);
        var entityTag = FormatEntityTag(result.Revision);
        if (entityTag is not null)
        {
            context.Response.Headers.ETag = entityTag;
        }

        switch (result.Status)
        {
            case StateReadStatus.NotFound:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            case StateReadStatus.Unavailable:
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            case StateReadStatus.Success:
                AddSchemaHeaders(context.Response, result.Schema);
                if (
                    condition is { } ifNoneMatch
                    && (
                        ifNoneMatch.Wildcard
                        || (
                            entityTag is not null
                            && string.Equals(
                                ifNoneMatch.Tag,
                                EntityTagHeaderValue.Parse(entityTag).Tag,
                                StringComparison.Ordinal
                            )
                        )
                    )
                )
                {
                    context.Response.StatusCode = StatusCodes.Status304NotModified;
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = options.ContentType;
                context.Response.ContentLength = result.Content.Length;
                if (!result.Content.IsEmpty)
                {
                    await context
                        .Response.Body.WriteAsync(result.Content, context.RequestAborted)
                        .ConfigureAwait(false);
                }

                return;
            default:
                throw new InvalidOperationException(
                    $"The resource reader returned unknown status '{result.Status}'."
                );
        }
    }

    private static async Task HandleWriteAsync(
        IResourceWriter writer,
        ValidatedOptions options,
        HttpContext context
    )
    {
        if (!HasExpectedContentType(context.Request, options.MediaType))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        if (
            !TryReadWriteCondition(context.Request, out var expectedRevision, out var checkRevision)
        )
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!TryReadSchema(context.Request, out var schema))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (
            options.MaximumRequestBodySize is { } maximumSize
            && context.Request.ContentLength is { } contentLength
            && contentLength > maximumSize
        )
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        using var content = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var bytesRead = await context
                    .Request.Body.ReadAsync(buffer, context.RequestAborted)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (
                    options.MaximumRequestBodySize is { } limit
                    && content.Length > limit - bytesRead
                )
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }

                await content
                    .WriteAsync(buffer.AsMemory(0, bytesRead), context.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        StateWriteResult result;
        try
        {
            result = await writer
                .WriteAsync(
                    new ResourceWriteRequest(
                        content.GetBuffer().AsMemory(0, checked((int)content.Length)),
                        expectedRevision,
                        schema,
                        checkRevision
                    ),
                    context.RequestAborted
                )
                .ConfigureAwait(false);
        }
        catch (StateConflictException)
        {
            context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            return;
        }

        var entityTag = FormatEntityTag(result.Revision);
        if (entityTag is not null)
        {
            context.Response.Headers.ETag = entityTag;
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static bool HasExpectedContentType(HttpRequest request, string expectedMediaType) =>
        MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
        && contentType is not null
        && string.Equals(
            contentType.MediaType,
            expectedMediaType,
            StringComparison.OrdinalIgnoreCase
        );

    private static bool TryReadIfNoneMatch(HttpRequest request, out ParsedIfNoneMatch? condition)
    {
        condition = null;
        var values = request.Headers.IfNoneMatch;
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count != 1)
        {
            return false;
        }

        var rawValue = values[0];
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var value = rawValue.Trim();
        if (value == "*")
        {
            condition = new ParsedIfNoneMatch(Wildcard: true, Tag: null);
            return true;
        }

        if (
            value.Contains(',')
            || !EntityTagHeaderValue.TryParse(value, out var entityTag)
            || entityTag is null
            || entityTag.Tag == "*"
        )
        {
            return false;
        }

        condition = new ParsedIfNoneMatch(Wildcard: false, entityTag.Tag);
        return true;
    }

    private static bool TryReadWriteCondition(
        HttpRequest request,
        out string? expectedRevision,
        out bool checkRevision
    )
    {
        expectedRevision = null;
        checkRevision = false;
        var ifMatchValues = request.Headers.IfMatch;
        var ifNoneMatchValues = request.Headers.IfNoneMatch;
        if (ifMatchValues.Count == 0 && ifNoneMatchValues.Count == 0)
        {
            return true;
        }

        if (
            ifMatchValues.Count > 0 && ifNoneMatchValues.Count > 0
            || ifMatchValues.Count > 1
            || ifNoneMatchValues.Count > 1
        )
        {
            return false;
        }

        if (ifMatchValues.Count == 1)
        {
            var rawValue = ifMatchValues[0];
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return false;
            }

            var value = rawValue.Trim();
            if (
                value.Contains(',')
                || !EntityTagHeaderValue.TryParse(value, out var entityTag)
                || entityTag is null
                || entityTag.IsWeak
                || !TryDecodeRevision(entityTag.Tag, out expectedRevision)
            )
            {
                return false;
            }

            checkRevision = true;
            return true;
        }

        if (ifNoneMatchValues.Count == 1)
        {
            var rawValue = ifNoneMatchValues[0];
            if (rawValue?.Trim() == "*")
            {
                checkRevision = true;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadSchema(HttpRequest request, out StateSchemaMetadata? schema)
    {
        schema = null;
        if (
            !TryReadSingleHeader(request, HttpResourceReader.SchemaIdHeaderName, out var modelId)
            || !TryReadSingleHeader(
                request,
                HttpResourceReader.SchemaVersionHeaderName,
                out var versionValue
            )
        )
        {
            return false;
        }

        if (modelId is null && versionValue is null)
        {
            return true;
        }

        if (
            versionValue is null
            || !int.TryParse(
                versionValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var version
            )
            || version < StateSchemaMetadata.InitialVersion
        )
        {
            return false;
        }

        schema = new StateSchemaMetadata(modelId, version);
        return true;
    }

    private static bool TryReadSingleHeader(HttpRequest request, string name, out string? value)
    {
        value = null;
        var values = request.Headers[name];
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count != 1)
        {
            return false;
        }

        var rawValue = values[0];
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        value = rawValue.Trim();
        return true;
    }

    private static void AddSchemaHeaders(HttpResponse response, StateSchemaMetadata? schema)
    {
        if (schema is not { } metadata)
        {
            return;
        }

        if (metadata.ModelId is not null)
        {
            response.Headers[HttpResourceReader.SchemaIdHeaderName] = metadata.ModelId;
        }

        response.Headers[HttpResourceReader.SchemaVersionHeaderName] = metadata.Version.ToString(
            CultureInfo.InvariantCulture
        );
    }

    private static string? FormatEntityTag(string? revision)
    {
        if (revision is null)
        {
            return null;
        }

        var bytes = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true
        ).GetBytes(revision);
        return $"\"cfg1.{Base64UrlEncode(bytes)}\"";
    }

    private static bool TryDecodeRevision(string entityTag, out string? revision)
    {
        revision = null;
        const string prefix = "\"cfg1.";
        if (!entityTag.StartsWith(prefix, StringComparison.Ordinal) || !entityTag.EndsWith('"'))
        {
            return false;
        }

        var value = entityTag[prefix.Length..^1];
        if (
            value.Any(static character =>
                character
                    is not (>= 'A' and <= 'Z')
                        and not (>= 'a' and <= 'z')
                        and not (>= '0' and <= '9')
                        and not '-'
                        and not '_'
            )
            || value.Length % 4 == 1
        )
        {
            return false;
        }

        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (!string.Equals(Base64UrlEncode(bytes), value, StringComparison.Ordinal))
            {
                return false;
            }

            revision = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true
            ).GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ValidatedOptions ValidateOptions(HttpResourceEndpointOptions options)
    {
        ValidateRelativePath(options.GetPath, nameof(options.GetPath));
        ValidateRelativePath(options.UpdatePath, nameof(options.UpdatePath));
        if (
            !MediaTypeHeaderValue.TryParse(options.ContentType, out var contentType)
            || contentType is null
        )
        {
            throw new ArgumentException("ContentType must be a valid media type.", nameof(options));
        }

        var mediaType = contentType.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            throw new ArgumentException("ContentType must be a valid media type.", nameof(options));
        }

        if (options.MaximumRequestBodySize is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumRequestBodySize must be between one and Int32.MaxValue bytes or null."
            );
        }

        return new ValidatedOptions(
            options.GetPath,
            options.UpdatePath,
            contentType.ToString(),
            mediaType,
            options.RequireAuthorization,
            options.MaximumRequestBodySize
        );
    }

    private static void ValidateRelativePath(string path, string parameterName)
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('?')
            || path.Contains('#')
            || path.Contains('\\')
            || !Uri.TryCreate(path, UriKind.Relative, out var relativePath)
            || relativePath.IsAbsoluteUri
        )
        {
            throw new ArgumentException(
                "Endpoint paths must be non-empty relative paths without a query, fragment, or root escape.",
                parameterName
            );
        }

        foreach (var segment in path.Split('/'))
        {
            var decodedSegment = Uri.UnescapeDataString(segment);
            if (
                decodedSegment is "." or ".."
                || decodedSegment.Contains('/')
                || decodedSegment.Contains('\\')
            )
            {
                throw new ArgumentException(
                    "Endpoint paths must remain under the route root.",
                    parameterName
                );
            }
        }
    }

    private static void ValidateRouteRoot(string routeRoot)
    {
        if (
            !routeRoot.StartsWith("/", StringComparison.Ordinal)
            || routeRoot.StartsWith("//", StringComparison.Ordinal)
            || routeRoot.Contains('?')
            || routeRoot.Contains('#')
            || routeRoot.Contains('\\')
        )
        {
            throw new ArgumentException(
                "The route root must be an absolute route path without a query or fragment.",
                nameof(routeRoot)
            );
        }

        try
        {
            _ = RoutePatternFactory.Parse(routeRoot);
        }
        catch (RoutePatternException exception)
        {
            throw new ArgumentException(
                "The route root is not a valid route pattern.",
                nameof(routeRoot),
                exception
            );
        }
    }

    private sealed record ValidatedOptions
    {
        public string GetPath { get; init; }
        public string UpdatePath { get; init; }
        public string ContentType { get; init; }
        public string MediaType { get; init; }
        public bool RequireAuthorization { get; init; }
        public long? MaximumRequestBodySize { get; init; }

        public ValidatedOptions(
            string GetPath,
            string UpdatePath,
            string ContentType,
            string MediaType,
            bool RequireAuthorization,
            long? MaximumRequestBodySize
        )
        {
            this.GetPath = GetPath;
            this.UpdatePath = UpdatePath;
            this.ContentType = ContentType;
            this.MediaType = MediaType;
            this.RequireAuthorization = RequireAuthorization;
            this.MaximumRequestBodySize = MaximumRequestBodySize;
        }

        public void Deconstruct(
            out string GetPath,
            out string UpdatePath,
            out string ContentType,
            out string MediaType,
            out bool RequireAuthorization,
            out long? MaximumRequestBodySize
        )
        {
            GetPath = this.GetPath;
            UpdatePath = this.UpdatePath;
            ContentType = this.ContentType;
            MediaType = this.MediaType;
            RequireAuthorization = this.RequireAuthorization;
            MaximumRequestBodySize = this.MaximumRequestBodySize;
        }
    }

    private sealed record ParsedIfNoneMatch
    {
        public bool Wildcard { get; init; }
        public string? Tag { get; init; }

        public ParsedIfNoneMatch(bool Wildcard, string? Tag)
        {
            this.Wildcard = Wildcard;
            this.Tag = Tag;
        }

        public void Deconstruct(out bool Wildcard, out string? Tag)
        {
            Wildcard = this.Wildcard;
            Tag = this.Tag;
        }
    }
}
