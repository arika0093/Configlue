using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Configlue.Hosting.AspNetCore;

/// <summary>Route registration helpers for the state endpoints.</summary>
internal static class StateEndpointRoutes
{
    public static string ToRoutePath(string relativePath) =>
        relativePath.Length == 0 ? "/" : relativePath;

    public static IEnumerable<string> CollectStatePaths(ValidatedStateEndpointOptions options)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (options.MapRead)
        {
            paths.Add(ToRoutePath(options.ReadPath));
        }

        if (options.MapWrite || options.MapPatch)
        {
            paths.Add(ToRoutePath(options.WritePath));
        }

        return paths;
    }

    public static IEnumerable<string> CollectAllowedMethods(
        ValidatedStateEndpointOptions options,
        string statePath
    )
    {
        if (
            options.MapRead
            && string.Equals(ToRoutePath(options.ReadPath), statePath, StringComparison.Ordinal)
        )
        {
            yield return "GET";
        }

        if (
            options.MapWrite
            && string.Equals(ToRoutePath(options.WritePath), statePath, StringComparison.Ordinal)
        )
        {
            yield return "PUT";
        }

        if (
            options.MapPatch
            && string.Equals(ToRoutePath(options.WritePath), statePath, StringComparison.Ordinal)
        )
        {
            yield return "PATCH";
        }
    }

    public static void SetAcceptPatch(HttpContext context) =>
        context.Response.Headers["Accept-Patch"] = "application/json-patch+json";

    public static string NormalizeForComparison(string path) => path.Trim('/').ToLowerInvariant();

    public static void ValidatePattern(string pattern)
    {
        if (
            !pattern.StartsWith("/", StringComparison.Ordinal)
            || pattern.StartsWith("//", StringComparison.Ordinal)
            || pattern.Contains('?')
            || pattern.Contains('#')
            || pattern.Contains('\\')
        )
        {
            throw new ArgumentException(
                "The route pattern must be an absolute route path without a query or fragment.",
                nameof(pattern)
            );
        }

        try
        {
            _ = RoutePatternFactory.Parse(pattern);
        }
        catch (RoutePatternException exception)
        {
            throw new ArgumentException(
                "The route pattern is not a valid route pattern.",
                nameof(pattern),
                exception
            );
        }
    }

    public static void ValidateStatePath(string path, string parameterName, bool allowEmpty)
    {
        if (path.Length == 0)
        {
            if (!allowEmpty)
            {
                throw new ArgumentException(
                    "Endpoint paths must be non-empty relative paths without a query, fragment, or root escape.",
                    parameterName
                );
            }

            return;
        }

        if (
            path.StartsWith("/", StringComparison.Ordinal)
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
}

/// <summary>Request/precondition validation stage: content types, ETags, If-Match rules.</summary>
/// <remarks>
/// Small request-scoped results keep HTTP semantics visible at the call site without
/// a generic middleware pipeline.
/// </remarks>
internal static class StateEndpointPreconditions
{
    public const string JsonMediaType = "application/json";
    public const string JsonPatchMediaType = "application/json-patch+json";

    public sealed record PutPrecondition(string? IfMatch);

    public sealed record PatchPrecondition(string IfMatch);

    public static string ComputeEtagHex(byte[] canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        return Convert.ToHexString(SHA256.HashData(canonicalJson)).ToLowerInvariant();
    }

    public static string FormatEtag(string hex) => $"\"{hex}\"";

    public static bool HasJsonContentType(HttpRequest request) =>
        HasMediaType(request, JsonMediaType);

    public static bool HasJsonPatchContentType(HttpRequest request) =>
        HasMediaType(request, JsonPatchMediaType);

    private static bool HasMediaType(HttpRequest request, string mediaType)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType))
        {
            return false;
        }

        return string.Equals(contentType.MediaType, mediaType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates PUT preconditions. Returns null when a 400 problem was written.
    /// PUT supports <c>If-Match</c> only; <c>If-None-Match</c> is rejected.
    /// </summary>
    public static async Task<PutPrecondition?> RequirePutPreconditionAsync(HttpContext context)
    {
        if (!TryReadIfMatch(context.Request, out var ifMatch, out var preconditionMalformed))
        {
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid precondition.",
                    "The If-Match header must be a single strong Configlue state ETag or '*', and must not be combined with If-None-Match."
                )
                .ConfigureAwait(false);
            return null;
        }

        if (preconditionMalformed)
        {
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid precondition.",
                    "The If-Match header must be a single strong Configlue state ETag or '*'."
                )
                .ConfigureAwait(false);
            return null;
        }

        if (context.Request.Headers.ContainsKey("If-None-Match"))
        {
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid precondition.",
                    "PUT supports If-Match only; If-None-Match is not supported for state writes."
                )
                .ConfigureAwait(false);
            return null;
        }

        return new PutPrecondition(ifMatch);
    }

    /// <summary>
    /// Validates PATCH preconditions. Returns null when a 4xx problem was written.
    /// PATCH requires a single strong effective-state <c>If-Match</c> ETag.
    /// </summary>
    public static async Task<PatchPrecondition?> RequirePatchPreconditionAsync(HttpContext context)
    {
        if (context.Request.Headers.IfMatch.Count == 0)
        {
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status428PreconditionRequired,
                    "Precondition required.",
                    "PATCH requires a strong effective-state If-Match ETag. Read the current state with GET first."
                )
                .ConfigureAwait(false);
            return null;
        }

        if (!TryReadIfMatch(context.Request, out var ifMatch, out var preconditionMalformed))
        {
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid precondition.",
                    "The If-Match header must be a single strong Configlue state ETag, and must not be combined with If-None-Match."
                )
                .ConfigureAwait(false);
            return null;
        }

        if (
            preconditionMalformed
            || string.Equals(ifMatch, "*", StringComparison.Ordinal)
            || context.Request.Headers.ContainsKey("If-None-Match")
        )
        {
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid precondition.",
                    "PATCH requires a single strong Configlue state ETag. Weak ETags, '*' and If-None-Match are not accepted as a patch baseline."
                )
                .ConfigureAwait(false);
            return null;
        }

        return new PatchPrecondition(ifMatch!);
    }

    public static bool TryReadIfMatch(HttpRequest request, out string? etag, out bool malformed)
    {
        etag = null;
        malformed = false;
        var values = request.Headers.IfMatch;
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count != 1)
        {
            return false;
        }

        var raw = values[0]?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        if (raw == "*")
        {
            etag = "*";
            return true;
        }

        if (raw.Contains(','))
        {
            return false;
        }

        if (!EntityTagHeaderValue.TryParse(raw, out var parsed) || parsed is null || parsed.IsWeak)
        {
            malformed = true;
            return true;
        }

        if (!TryExtractHex(parsed.Tag, out var hex))
        {
            malformed = true;
            return true;
        }

        etag = hex;
        return true;
    }

    public static bool IfNoneMatchMatches(HttpRequest request, string currentHex)
    {
        var values = request.Headers.IfNoneMatch;
        if (values.Count == 0)
        {
            return false;
        }

        foreach (var rawValue in values)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var value = rawValue.Trim();
            if (value == "*")
            {
                return true;
            }

            foreach (var part in value.Split(','))
            {
                var candidate = part.Trim();
                if (candidate.Length == 0)
                {
                    continue;
                }

                if (!EntityTagHeaderValue.TryParse(candidate, out var parsed) || parsed is null)
                {
                    continue;
                }

                if (
                    TryExtractHex(parsed.Tag, out var hex)
                    && string.Equals(hex, currentHex, StringComparison.Ordinal)
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool TryExtractHex(string tag, out string hex)
    {
        hex = "";
        if (tag.Length < 2 || !tag.StartsWith('"') || !tag.EndsWith('"'))
        {
            return false;
        }

        var inner = tag[1..^1];
        if (inner.Length != 64)
        {
            return false;
        }

        if (inner.Any(static c => !Uri.IsHexDigit(c)))
        {
            return false;
        }

        hex = inner.ToLowerInvariant();
        return true;
    }
}
