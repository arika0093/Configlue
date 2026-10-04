using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Configlue.Resource.Kubernetes;

/// <summary>
/// Kubernetes REST client backed by an injected <see cref="HttpClient"/>.
/// The <see cref="HttpClient"/> remains caller-owned and must already target the cluster
/// (base address, bearer token, TLS). Response bodies are never logged.
/// </summary>
public sealed class HttpKubernetesObjectClient : IKubernetesObjectClient
{
    private readonly HttpClient _httpClient;

    /// <summary>Creates a REST client over an already-configured HTTP client.</summary>
    /// <param name="httpClient">The configured HTTP client. It remains caller-owned.</param>
    public HttpKubernetesObjectClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<KubernetesConfigMapSnapshot> GetConfigMapAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken
    )
    {
        KubernetesResource.ValidateNamespace(@namespace);
        KubernetesResource.ValidateName(name);
        using var response = await _httpClient
            .GetAsync(ConfigMapPath(@namespace, name), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new KubernetesObjectNotFoundException(
                $"The ConfigMap '{@namespace}/{name}' was not found."
            );
        }

        EnsureSuccess(response, $"ConfigMap '{@namespace}/{name}'");
        var body = await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return ParseConfigMap(body);
    }

    /// <inheritdoc />
    public async Task<KubernetesSecretSnapshot> GetSecretAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken
    )
    {
        KubernetesResource.ValidateNamespace(@namespace);
        KubernetesResource.ValidateName(name);
        using var response = await _httpClient
            .GetAsync(SecretPath(@namespace, name), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new KubernetesObjectNotFoundException(
                $"The Secret '{@namespace}/{name}' was not found."
            );
        }

        EnsureSuccess(response, $"Secret '{@namespace}/{name}'");
        var body = await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return ParseSecret(body);
    }

    /// <inheritdoc />
    public async Task<string?> ReplaceConfigMapAsync(
        string @namespace,
        string name,
        IReadOnlyDictionary<string, string> data,
        IReadOnlyDictionary<string, byte[]> binaryData,
        string? expectedResourceVersion,
        bool requireMissing,
        CancellationToken cancellationToken
    )
    {
        KubernetesResource.ValidateNamespace(@namespace);
        KubernetesResource.ValidateName(name);
        var payload = BuildConfigMapPayload(data, binaryData, expectedResourceVersion);
        if (requireMissing)
        {
            using var create = await _httpClient
                .PostAsync(
                    CollectionPath("configmaps", @namespace),
                    new StringContent(payload, Encoding.UTF8, "application/json"),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (create.StatusCode == HttpStatusCode.Conflict)
            {
                throw new KubernetesConflictException(
                    $"The ConfigMap '{@namespace}/{name}' already exists."
                );
            }

            EnsureSuccess(create, $"ConfigMap '{@namespace}/{name}'");
            var created = ParseConfigMap(
                await ReadBodyAsync(create.Content, cancellationToken).ConfigureAwait(false)
            );
            return created.ResourceVersion;
        }

        using var replace = await _httpClient
            .PutAsync(
                ConfigMapPath(@namespace, name),
                new StringContent(payload, Encoding.UTF8, "application/json"),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (replace.StatusCode is HttpStatusCode.Conflict or (HttpStatusCode)422)
        {
            throw new KubernetesConflictException(
                $"The ConfigMap '{@namespace}/{name}' changed after it was read."
            );
        }

        if (replace.StatusCode == HttpStatusCode.Forbidden)
        {
            var detail = await ReadBodyAsync(replace.Content, cancellationToken)
                .ConfigureAwait(false);
            if (detail.Contains("immutable", StringComparison.OrdinalIgnoreCase))
            {
                throw new KubernetesImmutableException(
                    $"The ConfigMap '{@namespace}/{name}' is immutable."
                );
            }
        }

        EnsureSuccess(replace, $"ConfigMap '{@namespace}/{name}'");
        var updated = ParseConfigMap(
            await ReadBodyAsync(replace.Content, cancellationToken).ConfigureAwait(false)
        );
        return updated.ResourceVersion;
    }

    /// <inheritdoc />
    public async Task<string?> ReplaceSecretAsync(
        string @namespace,
        string name,
        IReadOnlyDictionary<string, byte[]> data,
        string? expectedResourceVersion,
        bool requireMissing,
        CancellationToken cancellationToken
    )
    {
        KubernetesResource.ValidateNamespace(@namespace);
        KubernetesResource.ValidateName(name);
        var payload = BuildSecretPayload(data, expectedResourceVersion);
        if (requireMissing)
        {
            using var create = await _httpClient
                .PostAsync(
                    CollectionPath("secrets", @namespace),
                    new StringContent(payload, Encoding.UTF8, "application/json"),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (create.StatusCode == HttpStatusCode.Conflict)
            {
                throw new KubernetesConflictException(
                    $"The Secret '{@namespace}/{name}' already exists."
                );
            }

            EnsureSuccess(create, $"Secret '{@namespace}/{name}'");
            var created = ParseSecret(
                await ReadBodyAsync(create.Content, cancellationToken).ConfigureAwait(false)
            );
            return created.ResourceVersion;
        }

        using var replace = await _httpClient
            .PutAsync(
                SecretPath(@namespace, name),
                new StringContent(payload, Encoding.UTF8, "application/json"),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (replace.StatusCode is HttpStatusCode.Conflict or (HttpStatusCode)422)
        {
            throw new KubernetesConflictException(
                $"The Secret '{@namespace}/{name}' changed after it was read."
            );
        }

        if (replace.StatusCode == HttpStatusCode.Forbidden)
        {
            var detail = await ReadBodyAsync(replace.Content, cancellationToken)
                .ConfigureAwait(false);
            if (detail.Contains("immutable", StringComparison.OrdinalIgnoreCase))
            {
                throw new KubernetesImmutableException(
                    $"The Secret '{@namespace}/{name}' is immutable."
                );
            }
        }

        EnsureSuccess(replace, $"Secret '{@namespace}/{name}'");
        var updated = ParseSecret(
            await ReadBodyAsync(replace.Content, cancellationToken).ConfigureAwait(false)
        );
        return updated.ResourceVersion;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KubernetesWatchEvent> WatchConfigMapAsync(
        string @namespace,
        string name,
        string? resourceVersion,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        KubernetesResource.ValidateNamespace(@namespace);
        KubernetesResource.ValidateName(name);
        await foreach (
            var watchEvent in WatchAsync(
                    "configmaps",
                    @namespace,
                    name,
                    resourceVersion,
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
        {
            if (
                watchEvent.ConfigMap is null
                && watchEvent.Type != KubernetesWatchType.Bookmark
                && watchEvent.Type != KubernetesWatchType.Error
            )
            {
                continue;
            }

            yield return watchEvent;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KubernetesWatchEvent> WatchSecretAsync(
        string @namespace,
        string name,
        string? resourceVersion,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        KubernetesResource.ValidateNamespace(@namespace);
        KubernetesResource.ValidateName(name);
        await foreach (
            var watchEvent in WatchAsync(
                    "secrets",
                    @namespace,
                    name,
                    resourceVersion,
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
        {
            if (
                watchEvent.Secret is null
                && watchEvent.Type != KubernetesWatchType.Bookmark
                && watchEvent.Type != KubernetesWatchType.Error
            )
            {
                continue;
            }

            yield return watchEvent;
        }
    }

    private async IAsyncEnumerable<KubernetesWatchEvent> WatchAsync(
        string resource,
        string @namespace,
        string name,
        string? resourceVersion,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var query =
            $"/api/v1/namespaces/{Uri.EscapeDataString(@namespace)}/{resource}"
            + $"?watch=true&fieldSelector={Uri.EscapeDataString($"metadata.name={name}")}"
            + (
                resourceVersion is null
                    ? string.Empty
                    : $"&resourceVersion={Uri.EscapeDataString(resourceVersion)}"
            );
        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Gone)
        {
            throw new KubernetesResourceExpiredException(
                $"The {resource} watch resourceVersion is expired."
            );
        }

        EnsureSuccess(response, resource);
#if NETSTANDARD2_0 || NETSTANDARD2_1
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#else
        using var stream = await response
            .Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
#endif
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
#if NETSTANDARD2_0 || NETSTANDARD2_1
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
#else
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
#endif
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            KubernetesWatchEvent? watchEvent;
            try
            {
                watchEvent =
                    resource == "configmaps" ? ParseConfigMapWatch(line) : ParseSecretWatch(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (watchEvent is not null)
            {
                yield return watchEvent;
            }
        }
    }

    private static string ConfigMapPath(string @namespace, string name) =>
        $"/api/v1/namespaces/{Uri.EscapeDataString(@namespace)}/configmaps/{Uri.EscapeDataString(name)}";

    private static string SecretPath(string @namespace, string name) =>
        $"/api/v1/namespaces/{Uri.EscapeDataString(@namespace)}/secrets/{Uri.EscapeDataString(name)}";

    private static string CollectionPath(string resource, string @namespace) =>
        $"/api/v1/namespaces/{Uri.EscapeDataString(@namespace)}/{resource}";

    private static Task<string> ReadBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken
    )
    {
#if NETSTANDARD2_0 || NETSTANDARD2_1
        _ = cancellationToken;
        return content.ReadAsStringAsync();
#else
        return content.ReadAsStringAsync(cancellationToken);
#endif
    }

    private static void EnsureSuccess(HttpResponseMessage response, string display)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new HttpRequestException(
            $"The Kubernetes request for {display} failed with {(int)response.StatusCode}."
        );
    }

    private static string BuildConfigMapPayload(
        IReadOnlyDictionary<string, string> data,
        IReadOnlyDictionary<string, byte[]> binaryData,
        string? resourceVersion
    )
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("apiVersion", "v1");
            writer.WriteString("kind", "ConfigMap");
            writer.WriteStartObject("metadata");
            if (resourceVersion is not null)
            {
                writer.WriteString("resourceVersion", resourceVersion);
            }

            writer.WriteEndObject();
            writer.WriteStartObject("data");
            foreach (var pair in data)
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            if (binaryData.Count > 0)
            {
                writer.WriteStartObject("binaryData");
                foreach (var pair in binaryData)
                {
                    writer.WriteString(pair.Key, Convert.ToBase64String(pair.Value));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildSecretPayload(
        IReadOnlyDictionary<string, byte[]> data,
        string? resourceVersion
    )
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("apiVersion", "v1");
            writer.WriteString("kind", "Secret");
            writer.WriteStartObject("metadata");
            if (resourceVersion is not null)
            {
                writer.WriteString("resourceVersion", resourceVersion);
            }

            writer.WriteEndObject();
            writer.WriteStartObject("data");
            foreach (var pair in data)
            {
                writer.WriteString(pair.Key, Convert.ToBase64String(pair.Value));
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static KubernetesConfigMapSnapshot ParseConfigMap(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var resourceVersion =
            root.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("resourceVersion", out var revision)
                ? revision.GetString()
                : null;
        var immutable =
            root.TryGetProperty("immutable", out var immutableElement)
            && immutableElement.ValueKind == JsonValueKind.True;
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (
            root.TryGetProperty("data", out var dataElement)
            && dataElement.ValueKind == JsonValueKind.Object
        )
        {
            foreach (var property in dataElement.EnumerateObject())
            {
                data[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        var binaryData = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (
            root.TryGetProperty("binaryData", out var binaryElement)
            && binaryElement.ValueKind == JsonValueKind.Object
        )
        {
            foreach (var property in binaryElement.EnumerateObject())
            {
                binaryData[property.Name] = Convert.FromBase64String(
                    property.Value.GetString() ?? string.Empty
                );
            }
        }

        return new KubernetesConfigMapSnapshot(resourceVersion, immutable, data, binaryData);
    }

    internal static KubernetesSecretSnapshot ParseSecret(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var resourceVersion =
            root.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("resourceVersion", out var revision)
                ? revision.GetString()
                : null;
        var immutable =
            root.TryGetProperty("immutable", out var immutableElement)
            && immutableElement.ValueKind == JsonValueKind.True;
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (
            root.TryGetProperty("data", out var dataElement)
            && dataElement.ValueKind == JsonValueKind.Object
        )
        {
            foreach (var property in dataElement.EnumerateObject())
            {
                data[property.Name] = Convert.FromBase64String(
                    property.Value.GetString() ?? string.Empty
                );
            }
        }

        return new KubernetesSecretSnapshot(resourceVersion, immutable, data);
    }

    private static KubernetesWatchEvent ParseConfigMapWatch(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;
        if (string.Equals(type, "BOOKMARK", StringComparison.Ordinal))
        {
            var bookmarkVersion =
                root.TryGetProperty("object", out var bookmarkObject)
                && bookmarkObject.TryGetProperty("metadata", out var bookmarkMetadata)
                && bookmarkMetadata.TryGetProperty("resourceVersion", out var bookmarkRevision)
                    ? bookmarkRevision.GetString()
                    : null;
            return new KubernetesWatchEvent(KubernetesWatchType.Bookmark, bookmarkVersion);
        }

        if (string.Equals(type, "ERROR", StringComparison.Ordinal))
        {
            var reason =
                root.TryGetProperty("object", out var reasonObject)
                && reasonObject.TryGetProperty("reason", out var reasonElement)
                    ? reasonElement.GetString()
                    : null;
            return new KubernetesWatchEvent(
                KubernetesWatchType.Error,
                null,
                errorCode: reason,
                errorMessage: null
            );
        }

        var kind = type switch
        {
            "ADDED" => KubernetesWatchType.Added,
            "DELETED" => KubernetesWatchType.Deleted,
            _ => KubernetesWatchType.Modified,
        };
        if (!root.TryGetProperty("object", out var payload))
        {
            return new KubernetesWatchEvent(kind, null);
        }

        var snapshot = ParseConfigMap(payload.GetRawText());
        return new KubernetesWatchEvent(kind, snapshot.ResourceVersion, configMap: snapshot);
    }

    private static KubernetesWatchEvent ParseSecretWatch(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;
        if (string.Equals(type, "BOOKMARK", StringComparison.Ordinal))
        {
            var bookmarkVersion =
                root.TryGetProperty("object", out var bookmarkObject)
                && bookmarkObject.TryGetProperty("metadata", out var bookmarkMetadata)
                && bookmarkMetadata.TryGetProperty("resourceVersion", out var bookmarkRevision)
                    ? bookmarkRevision.GetString()
                    : null;
            return new KubernetesWatchEvent(KubernetesWatchType.Bookmark, bookmarkVersion);
        }

        if (string.Equals(type, "ERROR", StringComparison.Ordinal))
        {
            var reason =
                root.TryGetProperty("object", out var reasonObject)
                && reasonObject.TryGetProperty("reason", out var reasonElement)
                    ? reasonElement.GetString()
                    : null;
            return new KubernetesWatchEvent(
                KubernetesWatchType.Error,
                null,
                errorCode: reason,
                errorMessage: null
            );
        }

        var kind = type switch
        {
            "ADDED" => KubernetesWatchType.Added,
            "DELETED" => KubernetesWatchType.Deleted,
            _ => KubernetesWatchType.Modified,
        };
        if (!root.TryGetProperty("object", out var payload))
        {
            return new KubernetesWatchEvent(kind, null);
        }

        var snapshot = ParseSecret(payload.GetRawText());
        return new KubernetesWatchEvent(kind, snapshot.ResourceVersion, secret: snapshot);
    }
}
