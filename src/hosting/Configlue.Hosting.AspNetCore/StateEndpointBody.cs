using System.Buffers;
using Microsoft.AspNetCore.Http;

namespace Configlue.Hosting.AspNetCore;

/// <summary>Bounded body-read stage enforcing the configured size limit.</summary>
internal static class StateEndpointBody
{
    /// <summary>Reads the request body, enforcing the configured size limit.</summary>
    /// <returns>The body bytes, or null when the body exceeds the configured limit.</returns>
    public static async Task<byte[]?> ReadRequestBodyAsync(
        HttpContext context,
        ValidatedStateEndpointOptions options
    )
    {
        if (
            options.MaximumRequestBodySize is { } maximumSize
            && context.Request.ContentLength is { } contentLength
            && contentLength > maximumSize
        )
        {
            return null;
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
                    return null;
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

        return content.ToArray();
    }

    /// <summary>Reads the body or writes 413 when the limit is exceeded.</summary>
    public static async Task<byte[]?> RequireBodyAsync(
        HttpContext context,
        ValidatedStateEndpointOptions options
    )
    {
        var body = await ReadRequestBodyAsync(context, options).ConfigureAwait(false);
        if (body is null)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }

        return body;
    }
}
