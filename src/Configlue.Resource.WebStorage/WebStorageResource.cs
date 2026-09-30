using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Configlue.Resources;
using Microsoft.JSInterop;

namespace Configlue.Resource.WebStorage;

/// <summary>Selects which browser storage area backs a WebStorage resource.</summary>
public enum WebStorageKind
{
    /// <summary>The <c>localStorage</c> area, shared across tabs of one origin.</summary>
    Local,

    /// <summary>The <c>sessionStorage</c> area, scoped to one browser tab.</summary>
    Session,
}

/// <summary>Thrown when browser storage cannot be reached, for example during prerender.</summary>
public sealed class WebStorageUnavailableException : InvalidOperationException
{
    /// <summary>Creates the exception with a reason.</summary>
    public WebStorageUnavailableException(string message)
        : base(message) { }

    /// <summary>Creates the exception with a reason and inner cause.</summary>
    public WebStorageUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Reads and writes one logical value through browser <c>localStorage</c> or <c>sessionStorage</c>.
/// </summary>
/// <remarks>
/// The resource uses the scoped <see cref="IJSRuntime"/> and must therefore be created for a
/// dependency-injection scope. During Blazor prerender, or after a Blazor Server circuit
/// disconnects, JavaScript is unavailable; reads report <see cref="StateReadStatus.Unavailable"/>
/// and writes throw <see cref="WebStorageUnavailableException"/> instead of silently using a
/// different storage.
/// </remarks>
public sealed class WebStorageResource : IContextualResourceReader, IContextualResourceWriter
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new(JsonSerializerDefaults.Web);
    private readonly IJSRuntime _jsRuntime;
    private readonly Func<ConfiglueResourceContext, string>? _keySelector;

    /// <summary>Creates a resource over one browser storage area and key.</summary>
    public WebStorageResource(
        IJSRuntime jsRuntime,
        WebStorageKind kind,
        string key,
        Func<ConfiglueResourceContext, string>? keySelector = null
    )
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _jsRuntime = jsRuntime;
        Kind = kind;
        Key = key;
        _keySelector = keySelector;
    }

    /// <summary>The browser storage area backing this resource.</summary>
    public WebStorageKind Kind { get; }

    /// <summary>The base storage key.</summary>
    public string Key { get; }

    private string StorageName => Kind == WebStorageKind.Local ? "localStorage" : "sessionStorage";

    /// <inheritdoc />
    public ValueTask<ResourceReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(ConfiglueResourceContext.Default, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var raw = await _jsRuntime
                .InvokeAsync<string?>(
                    StorageName + ".getItem",
                    cancellationToken,
                    [ResolveKey(context)]
                )
                .ConfigureAwait(false);
            return string.IsNullOrEmpty(raw) ? ResourceReadResult.NotFound() : Decode(raw);
        }
        catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
        {
            return ResourceReadResult.Unavailable();
        }
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var key = ResolveKey(context);
            if (!request.Condition.IsNone)
            {
                var existing = await _jsRuntime
                    .InvokeAsync<string?>(StorageName + ".getItem", cancellationToken, [key])
                    .ConfigureAwait(false);
                var currentRevision = string.IsNullOrEmpty(existing)
                    ? null
                    : Decode(existing).Revision;
                if (request.Condition.IsMustNotExist)
                {
                    if (existing is not null)
                    {
                        throw new StateConflictException(
                            $"The browser storage key '{key}' already exists."
                        );
                    }
                }
                else if (
                    request.Condition.IsMatch
                    && !string.Equals(
                        currentRevision,
                        request.Condition.Revision,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new StateConflictException(
                        $"The browser storage key '{key}' changed after it was read."
                    );
                }
            }

            var revision = Guid.NewGuid().ToString("N");
            await _jsRuntime
                .InvokeVoidAsync(
                    StorageName + ".setItem",
                    cancellationToken,
                    [key, Encode(revision, request.Content)]
                )
                .ConfigureAwait(false);
            return new StateWriteResult(revision);
        }
        catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
        {
            throw new WebStorageUnavailableException(
                "Browser storage is not available. This can happen during prerender or after a Blazor Server circuit disconnects.",
                exception
            );
        }
    }

    private string ResolveKey(ConfiglueResourceContext context)
    {
        if (_keySelector is not null)
        {
            var selected = _keySelector(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(selected);
            return selected;
        }

        return context.Key.IsDefault ? Key : Key + ":" + context.Key.Value;
    }

    private static bool IsUnavailable(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is JSDisconnectedException or InvalidOperationException)
        {
            return true;
        }

        return exception is OperationCanceledException
            && !cancellationToken.IsCancellationRequested;
    }

    private static ResourceReadResult Decode(string raw)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<WebStorageEnvelope>(raw, EnvelopeOptions);
            if (envelope?.Content is not null)
            {
                return ResourceReadResult.Success(
                    Convert.FromBase64String(envelope.Content),
                    envelope.Revision
                );
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            // Values written outside Configlue are treated as raw payload without an envelope.
        }

        var content = Encoding.UTF8.GetBytes(raw);
        return ResourceReadResult.Success(content, CreateRawRevision(raw));
    }

    private static string Encode(string revision, ReadOnlyMemory<byte> content)
    {
        var envelope = new WebStorageEnvelope
        {
            Revision = revision,
            Content = Convert.ToBase64String(content.Span),
        };
        return JsonSerializer.Serialize(envelope, EnvelopeOptions);
    }

    private static string CreateRawRevision(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    private sealed class WebStorageEnvelope
    {
        public string? Revision { get; set; }

        public string? Content { get; set; }
    }
}
