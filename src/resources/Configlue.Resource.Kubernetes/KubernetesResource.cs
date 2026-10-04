using System.Security.Cryptography;
using System.Text;
using Configlue.State;

namespace Configlue.Resource.Kubernetes;

/// <summary>
/// Reads and writes one Kubernetes ConfigMap or Secret entry (or whole object) through a shared client.
/// </summary>
/// <remarks>
/// <para>
/// Kubernetes <c>resourceVersion</c> values are exposed as revisions and used for conditional writes:
/// stale writes fail with <see cref="StateConflictException"/> instead of silently overwriting a
/// newer object. Changing one key preserves unrelated keys; replacement details never surface in
/// application-facing state APIs.
/// </para>
/// <para>Read behavior is explicit:</para>
/// <list type="bullet">
/// <item>missing object maps to <c>NotFound</c>;</item>
/// <item>missing key in an existing object maps to <c>NotFound</c> with the object revision;</item>
/// <item>immutable objects read normally but reject writes with <see cref="KubernetesImmutableException"/>;</item>
/// <item>resolving a different namespace, name, or key addresses a different physical object.</item>
/// </list>
/// <para>
/// Minimal RBAC: reads and watches require <c>get</c>, <c>list</c>, and <c>watch</c> on
/// <c>configmaps</c> (or <c>secrets</c>) in the target namespace; writable mode additionally
/// requires <c>create</c>, <c>update</c>, and <c>patch</c>. For example:
/// </para>
/// <code>
/// apiVersion: rbac.authorization.k8s.io/v1
/// kind: Role
/// metadata: { namespace: app-config, name: configlue-reader }
/// rules:
///   - apiGroups: [""]  resources: ["configmaps"]  verbs: ["get", "list", "watch"]
///   - apiGroups: [""]  resources: ["secrets"]     verbs: ["get", "list", "watch"]
/// # writable mode adds:
/// # - apiGroups: [""]  resources: ["configmaps", "secrets"]  verbs: ["create", "update", "patch"]
/// </code>
/// <para>
/// Secret values, bearer tokens, and certificates never appear in <see cref="ToString"/>,
/// resource identities, physical origins, or exception messages.
/// </para>
/// </remarks>
public sealed class KubernetesResource
    : IResourceReader,
        IResourceWriter,
        IResourceIdentity,
        ISourceWatcher,
        IDisposable
{
    private readonly IKubernetesObjectClient _client;
    private readonly KubernetesResourceKind _kind;
    private readonly string _namespace;
    private readonly string _name;
    private readonly string? _key;
    private readonly KubernetesResourceOptions _options;
    private readonly TaskCompletionSource<bool> _disposeSignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _disposed;

    /// <summary>Creates a resource for one ConfigMap or Secret key (or whole object when key is null).</summary>
    /// <param name="client">The shared object client. It remains caller-owned.</param>
    /// <param name="kind">Whether this resource reads a ConfigMap or a Secret.</param>
    /// <param name="namespace">The object namespace.</param>
    /// <param name="name">The object name.</param>
    /// <param name="key">
    /// The entry key, or null for whole-object mode. Whole-object mode maps the complete key
    /// set deterministically (sorted keys, binary entries as base64 JSON).
    /// </param>
    /// <param name="options">Namespace, name, key, and watch settings.</param>
    public KubernetesResource(
        IKubernetesObjectClient client,
        KubernetesResourceKind kind,
        string @namespace,
        string name,
        string? key,
        KubernetesResourceOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateKind(kind);
        ValidateNamespace(@namespace);
        ValidateName(name);
        if (key is not null)
        {
            ValidateKey(key);
        }

        _client = client;
        _kind = kind;
        _namespace = @namespace;
        _name = name;
        _key = key;
        _options = options ?? new KubernetesResourceOptions();
        _options.Validate(keyRequired: false);
    }

    /// <summary>The object kind.</summary>
    public KubernetesResourceKind Kind => _kind;

    /// <summary>The configured namespace.</summary>
    public string Namespace => _namespace;

    /// <summary>The configured object name.</summary>
    public string Name => _name;

    /// <summary>The entry key, or null for whole-object mode.</summary>
    public string? Key => _key;

    /// <summary>Whether this resource maps the whole object instead of one key.</summary>
    public bool IsWholeObject => _key is null;

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context)
    {
        if (_options.FixedResourceId is { } explicitId)
        {
            return explicitId;
        }

        var address = ResolveAddress(context);
        return CreateResourceId(_kind, address.Namespace, address.Name, address.Key);
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var address = ResolveAddress(context);
        if (_kind == KubernetesResourceKind.ConfigMap)
        {
            KubernetesConfigMapSnapshot snapshot;
            try
            {
                snapshot = await _client
                    .GetConfigMapAsync(address.Namespace, address.Name, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KubernetesObjectNotFoundException)
            {
                return ResourceReadResult.NotFound();
            }

            if (address.Key is null)
            {
                var content = KubernetesObjectEncoding.EncodeConfigMapObject(
                    snapshot.Data,
                    snapshot.BinaryData
                );
                return ResourceReadResult.Success(content, snapshot.ResourceVersion);
            }

            if (snapshot.Data.TryGetValue(address.Key, out var text))
            {
                return ResourceReadResult.Success(
                    Encoding.UTF8.GetBytes(text),
                    snapshot.ResourceVersion
                );
            }

            if (snapshot.BinaryData.TryGetValue(address.Key, out var binary))
            {
                return ResourceReadResult.Success(binary.ToArray(), snapshot.ResourceVersion);
            }

            return ResourceReadResult.NotFound(snapshot.ResourceVersion);
        }
        else
        {
            KubernetesSecretSnapshot snapshot;
            try
            {
                snapshot = await _client
                    .GetSecretAsync(address.Namespace, address.Name, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KubernetesObjectNotFoundException)
            {
                return ResourceReadResult.NotFound();
            }

            if (address.Key is null)
            {
                var content = KubernetesObjectEncoding.EncodeSecretObject(snapshot.Data);
                return ResourceReadResult.Success(content, snapshot.ResourceVersion);
            }

            if (snapshot.Data.TryGetValue(address.Key, out var binary))
            {
                return ResourceReadResult.Success(binary.ToArray(), snapshot.ResourceVersion);
            }

            return ResourceReadResult.NotFound(snapshot.ResourceVersion);
        }
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var address = ResolveAddress(context);
        var requireMissing = request.Condition.IsMustNotExist;
        var expectedRevision = request.Condition.IsMatch ? request.Condition.Revision : null;
        if (_kind == KubernetesResourceKind.ConfigMap)
        {
            KubernetesConfigMapSnapshot? current = await TryGetConfigMapAsync(
                    address,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (current?.Immutable == true)
            {
                throw new KubernetesImmutableException(
                    $"The ConfigMap '{address.Namespace}/{address.Name}' is immutable."
                );
            }

            if (requireMissing && current is not null)
            {
                throw new KubernetesConflictException(
                    $"The ConfigMap '{address.Namespace}/{address.Name}' already exists."
                );
            }

            if (
                expectedRevision is not null
                && !string.Equals(
                    current?.ResourceVersion,
                    expectedRevision,
                    StringComparison.Ordinal
                )
            )
            {
                throw new KubernetesConflictException(
                    $"The ConfigMap '{address.Namespace}/{address.Name}' changed after it was read."
                );
            }

            var data = new Dictionary<string, string>(StringComparer.Ordinal);
            var binaryData = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (current is not null)
            {
                foreach (var pair in current.Data)
                {
                    data[pair.Key] = pair.Value;
                }

                foreach (var pair in current.BinaryData)
                {
                    binaryData[pair.Key] = pair.Value;
                }
            }

            if (address.Key is null)
            {
                KubernetesObjectEncoding.DecodeConfigMapObject(
                    request.Content,
                    out var next,
                    out var nextBinary
                );
                data = next;
                binaryData = nextBinary;
            }
            else
            {
                if (!IsValidUtf8(request.Content.Span))
                {
                    throw new InvalidDataException(
                        $"The ConfigMap key '{address.Key}' must be valid UTF-8 text."
                    );
                }

                data[address.Key] = Encoding.UTF8.GetString(request.Content.Span);
                binaryData.Remove(address.Key);
            }

            var revision = await _client
                .ReplaceConfigMapAsync(
                    address.Namespace,
                    address.Name,
                    data,
                    binaryData,
                    expectedRevision,
                    requireMissing,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return new StateWriteResult(revision);
        }
        else
        {
            KubernetesSecretSnapshot? current = await TryGetSecretAsync(address, cancellationToken)
                .ConfigureAwait(false);
            if (current?.Immutable == true)
            {
                throw new KubernetesImmutableException(
                    $"The Secret '{address.Namespace}/{address.Name}' is immutable."
                );
            }

            if (requireMissing && current is not null)
            {
                throw new KubernetesConflictException(
                    $"The Secret '{address.Namespace}/{address.Name}' already exists."
                );
            }

            if (
                expectedRevision is not null
                && !string.Equals(
                    current?.ResourceVersion,
                    expectedRevision,
                    StringComparison.Ordinal
                )
            )
            {
                throw new KubernetesConflictException(
                    $"The Secret '{address.Namespace}/{address.Name}' changed after it was read."
                );
            }

            Dictionary<string, byte[]> data;
            if (address.Key is null)
            {
                data = KubernetesObjectEncoding.DecodeSecretObject(request.Content);
            }
            else
            {
                data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                if (current is not null)
                {
                    foreach (var pair in current.Data)
                    {
                        data[pair.Key] = pair.Value;
                    }
                }

                data[address.Key] = request.Content.ToArray();
            }

            var revision = await _client
                .ReplaceSecretAsync(
                    address.Namespace,
                    address.Name,
                    data,
                    expectedRevision,
                    requireMissing,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return new StateWriteResult(revision);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A watch event is an invalidation signal; callers converge by re-reading current state.
    /// Bookmarks advance the resumed resourceVersion without signalling. An expired resourceVersion
    /// triggers a fresh GET and resubscription; when the fresh revision already differs from the
    /// observed revision the waiter returns immediately. Stream termination without an event
    /// reconnects with backoff.
    /// </remarks>
    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        var address = ResolveAddress(context);
        return WaitForChangeCoreAsync(address, observedRevision, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeSignal.TrySetResult(true);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var scope = _key ?? "<object>";
        return _kind == KubernetesResourceKind.ConfigMap
            ? $"k8s:configmap:{_namespace}/{_name}/{scope}"
            : $"k8s:secret:{_namespace}/{_name}/{scope}";
    }

    private async ValueTask WaitForChangeCoreAsync(
        KubernetesAddress address,
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watchTask = WatchLoopAsync(address, observedRevision, linked.Token);
        var completed = await Task.WhenAny(watchTask, _disposeSignal.Task).ConfigureAwait(false);
        if (completed == _disposeSignal.Task)
        {
            try
            {
                await linked.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }

            try
            {
                await watchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        await watchTask.ConfigureAwait(false);
    }

    private async Task WatchLoopAsync(
        KubernetesAddress address,
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        var resumeVersion = observedRevision;
        if (resumeVersion is null)
        {
            resumeVersion = await TryGetCurrentRevisionAsync(address, cancellationToken)
                .ConfigureAwait(false);
        }

        var delay = _options.WatchReconnectInitialDelay;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool streamSawEvent;
            try
            {
                streamSawEvent = await WatchOnceAsync(address, resumeVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KubernetesResourceExpiredException)
            {
                var fresh = await TryGetCurrentRevisionAsync(address, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(fresh, observedRevision, StringComparison.Ordinal))
                {
                    return;
                }

                resumeVersion = fresh;
                delay = _options.WatchReconnectInitialDelay;
                continue;
            }

            if (streamSawEvent)
            {
                return;
            }

            var current = await TryGetCurrentRevisionAsync(address, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(current, observedRevision, StringComparison.Ordinal))
            {
                return;
            }

            resumeVersion = current;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            delay = TimeSpan.FromTicks(
                Math.Min(delay.Ticks * 2, _options.WatchReconnectMaxDelay.Ticks)
            );
        }
    }

    private async Task<bool> WatchOnceAsync(
        KubernetesAddress address,
        string? resourceVersion,
        CancellationToken cancellationToken
    )
    {
        var stream =
            _kind == KubernetesResourceKind.ConfigMap
                ? _client.WatchConfigMapAsync(
                    address.Namespace,
                    address.Name,
                    resourceVersion,
                    cancellationToken
                )
                : _client.WatchSecretAsync(
                    address.Namespace,
                    address.Name,
                    resourceVersion,
                    cancellationToken
                );
        var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                var watchEvent = enumerator.Current;
                if (watchEvent is null)
                {
                    continue;
                }

                if (watchEvent.Type == KubernetesWatchType.Bookmark)
                {
                    continue;
                }

                if (watchEvent.Type == KubernetesWatchType.Error)
                {
                    if (watchEvent.IsExpired)
                    {
                        throw new KubernetesResourceExpiredException(
                            _kind == KubernetesResourceKind.ConfigMap
                                ? $"The ConfigMap '{address.Namespace}/{address.Name}' watch expired."
                                : $"The Secret '{address.Namespace}/{address.Name}' watch expired."
                        );
                    }

                    return false;
                }

                return true;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        return false;
    }

    private async Task<string?> TryGetCurrentRevisionAsync(
        KubernetesAddress address,
        CancellationToken cancellationToken
    )
    {
        if (_kind == KubernetesResourceKind.ConfigMap)
        {
            var snapshot = await TryGetConfigMapAsync(address, cancellationToken)
                .ConfigureAwait(false);
            return snapshot?.ResourceVersion;
        }

        var secret = await TryGetSecretAsync(address, cancellationToken).ConfigureAwait(false);
        return secret?.ResourceVersion;
    }

    private async Task<KubernetesConfigMapSnapshot?> TryGetConfigMapAsync(
        KubernetesAddress address,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await _client
                .GetConfigMapAsync(address.Namespace, address.Name, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KubernetesObjectNotFoundException)
        {
            return null;
        }
    }

    private async Task<KubernetesSecretSnapshot?> TryGetSecretAsync(
        KubernetesAddress address,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await _client
                .GetSecretAsync(address.Namespace, address.Name, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KubernetesObjectNotFoundException)
        {
            return null;
        }
    }

    private KubernetesAddress ResolveAddress(ConfiglueResourceContext context)
    {
        var @namespace = _options.NamespaceSelector?.Invoke(context) ?? _namespace;
        var name = _options.NameSelector?.Invoke(context) ?? _name;
        var key = _options.KeySelector?.Invoke(context) ?? _key;
        ValidateNamespace(@namespace);
        ValidateName(name);
        if (key is not null)
        {
            ValidateKey(key);
        }
        else if (_key is not null)
        {
            throw new InvalidOperationException(
                "A key selector returned null for a single-key resource."
            );
        }

        return new KubernetesAddress(@namespace, name, key);
    }

    private static ResourceId CreateResourceId(
        KubernetesResourceKind kind,
        string @namespace,
        string name,
        string? key
    )
    {
        var identity = Encoding.UTF8.GetBytes(
            (kind == KubernetesResourceKind.ConfigMap ? "configmap\n" : "secret\n")
                + @namespace
                + "\n"
                + name
                + "\n"
                + (key ?? string.Empty)
        );
        return new ResourceId(
            $"k8s:{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}"
        );
    }

    private static void ValidateKind(KubernetesResourceKind kind)
    {
        if (kind != KubernetesResourceKind.ConfigMap && kind != KubernetesResourceKind.Secret)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    internal static void ValidateNamespace(string @namespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        if (@namespace.Length > 253 || @namespace.Contains('\0') || @namespace.Contains('/'))
        {
            throw new ArgumentException(
                "A Kubernetes namespace must be a valid DNS label.",
                nameof(@namespace)
            );
        }
    }

    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 253 || name.Contains('\0') || name.Contains('/'))
        {
            throw new ArgumentException(
                "A Kubernetes object name must not be empty.",
                nameof(name)
            );
        }
    }

    internal static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Contains('\0') || key.Length > 253)
        {
            throw new ArgumentException(
                "A Kubernetes ConfigMap or Secret key must not be empty.",
                nameof(key)
            );
        }
    }

    private static readonly Encoding StrictUtf8 = Encoding.GetEncoding(
        "utf-8",
        EncoderFallback.ExceptionFallback,
        DecoderFallback.ExceptionFallback
    );

    private static bool IsValidUtf8(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            return true;
        }

        try
        {
            StrictUtf8.GetString(content.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record KubernetesAddress(string Namespace, string Name, string? Key);
}
