using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SparseFragments.JsonPatch;

namespace Configlue.Hosting.AspNetCore;

/// <summary>
/// Response/problem mapping stage. Centralizes the Configlue exception to HTTP
/// problem mapping where status/title semantics are genuinely shared by PUT/PATCH.
/// </summary>
internal static class StateEndpointProblems
{
    public static Task WriteValidationProblemAsync(
        HttpContext context,
        ConfiglueValidationException exception
    )
    {
        return WriteProblemAsync(
            context,
            StatusCodes.Status422UnprocessableEntity,
            "State validation failed.",
            string.Join("; ", exception.Failures)
        );
    }

    public static async Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail
    )
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        var problem = new
        {
            title,
            status = statusCode,
            detail,
        };
        await JsonSerializer
            .SerializeAsync(
                context.Response.Body,
                problem,
                new JsonSerializerOptions(),
                context.RequestAborted
            )
            .ConfigureAwait(false);
    }

    /// <summary>Maps state-read failures. Always handles the exception.</summary>
    public static Task WriteReadProblemAsync(HttpContext context, Exception exception)
    {
        return exception switch
        {
            ConfiglueValidationException validation => WriteValidationProblemAsync(
                context,
                validation
            ),
            InvalidOperationException invalid => WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "State read failed.",
                invalid.Message
            ),
            _ => WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "State read failed.",
                exception.Message
            ),
        };
    }

    /// <summary>Maps full-document decode failures (PUT body / fragment deserialization).</summary>
    public static Task WriteDecodeProblemAsync(HttpContext context, Exception exception)
    {
        return exception switch
        {
            JsonException json => WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Malformed state payload.",
                json.Message
            ),
            InvalidOperationException invalid => WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Malformed state payload.",
                invalid.Message
            ),
            _ => WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "State read failed.",
                exception.Message
            ),
        };
    }

    /// <summary>Maps missing-service failures shared by all write paths.</summary>
    public static Task WriteServicesProblemAsync(
        HttpContext context,
        InvalidOperationException exception
    )
    {
        return WriteProblemAsync(
            context,
            StatusCodes.Status500InternalServerError,
            "State services are not registered.",
            exception.Message
        );
    }

    /// <summary>
    /// Maps edit-session commit failures shared by PUT and PATCH.
    /// Covers validation (422), write conflicts (409), multi-write failures (409).
    /// </summary>
    public static Task WriteCommitProblemAsync(HttpContext context, Exception exception)
    {
        return exception switch
        {
            ConfiglueValidationException validation => WriteValidationProblemAsync(
                context,
                validation
            ),
            StateConflictException conflict => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "The state write conflicts with a concurrent change.",
                conflict.Message
            ),
            StateMultiWriteException multiWrite => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "A multi-source write failed after partial completion.",
                multiWrite.Message
            ),
            InvalidOperationException invalid => WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "State write failed.",
                invalid.Message
            ),
            _ => WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "State write failed.",
                exception.Message
            ),
        };
    }

    /// <summary>Maps write-preview failures (RFC 5789 atomicity gate) shared by PATCH paths.</summary>
    public static Task WritePreviewProblemAsync(HttpContext context, Exception exception)
    {
        return exception switch
        {
            ConfiglueValidationException validation => WriteValidationProblemAsync(
                context,
                validation
            ),
            StateConflictException conflict => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "The state write conflicts with a concurrent change.",
                conflict.Message
            ),
            NotSupportedException notSupported => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "The requested State PATCH spans multiple non-transactional write resources.",
                notSupported.Message
            ),
            InvalidOperationException invalid => WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "State write preview failed.",
                invalid.Message
            ),
            _ => WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "State write preview failed.",
                exception.Message
            ),
        };
    }

    /// <summary>Maps JSON Patch apply failures: malformed (400) vs inapplicable (409).</summary>
    public static Task WritePatchApplyProblemAsync(
        HttpContext context,
        JsonPatchException exception
    )
    {
        return
            exception.Kind
                is JsonPatchErrorKind.MalformedDocument
                    or JsonPatchErrorKind.MalformedPointer
                    or JsonPatchErrorKind.UnknownOperation
            ? WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Malformed JSON Patch document.",
                exception.Message
            )
            : WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "The JSON Patch cannot be applied to the current state.",
                exception.Message
            );
    }

    /// <summary>Maps If-Match mismatches shared by PUT and PATCH (412).</summary>
    public static Task WritePreconditionFailedProblemAsync(HttpContext context)
    {
        return WriteProblemAsync(
            context,
            StatusCodes.Status412PreconditionFailed,
            "The effective state changed after it was read.",
            "The If-Match ETag does not match the current effective state."
        );
    }

    /// <summary>Maps non-atomic multi-resource plans rejected before any write (409).</summary>
    public static Task WriteNonAtomicProblemAsync(HttpContext context, int physicalWriteCount)
    {
        return WriteProblemAsync(
            context,
            StatusCodes.Status409Conflict,
            "The requested State PATCH spans multiple non-transactional write resources.",
            $"The patch would require {physicalWriteCount} independent physical writes with no transactional batch guarantee, so it was rejected before writing anything."
        );
    }
}
