using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Configlue.Resources;
using Microsoft.JSInterop;

namespace Configlue.Hosting.Blazor;

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
/// Thrown when a conditional write cannot be enforced atomically on the current browser.
/// </summary>
/// <remarks>
/// The Web Locks API is required to serialize the read/compare/write critical section across
/// browser execution contexts. When it is unavailable the resource rejects conditional writes
/// instead of emulating compare-and-swap with a racy read-then-write. Unconditional writes remain
/// available.
/// </remarks>
public sealed class WebStorageAtomicityNotSupportedException : NotSupportedException
{
    /// <summary>Creates the exception with a reason.</summary>
    public WebStorageAtomicityNotSupportedException(string message)
        : base(message) { }

    /// <summary>Creates the exception with a reason and inner cause.</summary>
    public WebStorageAtomicityNotSupportedException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Reads and writes one logical value through browser <c>localStorage</c> or <c>sessionStorage</c>.
/// </summary>
/// <remarks>
/// <para>
/// The resource uses the scoped <see cref="IJSRuntime"/> and must therefore be created for a
/// dependency-injection scope. During Blazor prerender, or after a Blazor Server circuit
/// disconnects, JavaScript is unavailable; reads report <see cref="StateReadStatus.Unavailable"/>
/// and writes throw <see cref="WebStorageUnavailableException"/> instead of silently using a
/// different storage.
/// </para>
/// <para>
/// Conditional writes (<see cref="RevisionCondition.Match"/> and
/// <see cref="RevisionCondition.MustNotExist"/>) are performed as a single browser-side mutation.
/// The browser helper takes the browser-wide Web Locks exclusive lock named for the storage area
/// and resolved key, then reads the current envelope, validates the precondition, and writes the
/// new envelope inside one <c>navigator.locks.request</c> callback. Returning from the callback
/// releases the lock, so the lock lifetime is bound to the mutation itself rather than to a
/// timeout that could expire while a commit is still possible. This coordinates every cooperating
/// writer for that key regardless of tab, window, iframe, Blazor Server circuit, WebAssembly
/// realm, or component scope. Two writers that observed the same revision therefore cannot both
/// commit; the losing writer receives <see cref="StateConflictException"/>.
/// </para>
/// <para>
/// The Web Locks API requires the host page to reference
/// <c>_content/Configlue.Hosting.Blazor/configlue-webstorage.js</c>. If the API is missing, or the
/// script was not loaded, conditional writes throw <see cref="WebStorageAtomicityNotSupportedException"/>
/// rather than falling back to a non-atomic read-then-write. Unconditional writes do not take the
/// lock and keep their last-writer-wins semantics.
/// </para>
/// </remarks>
public sealed class WebStorageResource : IResourceReader, IResourceWriter
{
    private const string MutateIdentifier = "configlueWebStorage.mutate";
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
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var key = ResolveKey(context);
        var revision = Guid.NewGuid().ToString("N");
        var encoded = Encode(revision, request.Content);
        try
        {
            if (request.Condition.IsNone)
            {
                await _jsRuntime
                    .InvokeVoidAsync(StorageName + ".setItem", cancellationToken, [key, encoded])
                    .ConfigureAwait(false);
                return new StateWriteResult(revision);
            }

            var status = await MutateAsync(key, encoded, request.Condition, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(status, "committed", StringComparison.Ordinal))
            {
                return new StateWriteResult(revision);
            }

            if (string.Equals(status, "conflict", StringComparison.Ordinal))
            {
                throw new StateConflictException(
                    $"The browser storage key '{key}' does not satisfy the requested revision condition."
                );
            }

            if (string.Equals(status, "unavailable", StringComparison.Ordinal))
            {
                throw new JSException("The browser storage mutation failed.");
            }

            throw new WebStorageAtomicityNotSupportedException(
                $"The browser does not expose the Web Locks API, so the browser storage key '{key}' cannot be written atomically under a revision precondition. "
                    + "Reference _content/Configlue.Hosting.Blazor/configlue-webstorage.js from the host page, or use unconditional writes."
            );
        }
        catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
        {
            throw new WebStorageUnavailableException(
                "Browser storage is not available. This can happen during prerender or after a Blazor Server circuit disconnects.",
                exception
            );
        }
    }

    private async ValueTask<string?> MutateAsync(
        string key,
        string encoded,
        RevisionCondition condition,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await _jsRuntime
                .InvokeAsync<string?>(
                    MutateIdentifier,
                    cancellationToken,
                    [StorageName, key, encoded, condition.Revision, condition.IsMustNotExist]
                )
                .ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw new WebStorageAtomicityNotSupportedException(
                "The browser-side configlueWebStorage helper is unavailable, so the write precondition cannot be enforced atomically. "
                    + "Reference _content/Configlue.Hosting.Blazor/configlue-webstorage.js from the host page.",
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
        if (exception is JSException or InvalidOperationException)
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
