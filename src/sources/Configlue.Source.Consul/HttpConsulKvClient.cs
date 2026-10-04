using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Configlue.Source.Consul;

/// <summary>
/// Default <see cref="IConsulKvClient"/> over HTTP. The supplied <see cref="HttpClient"/> and
/// handler remain caller-owned and are never disposed by this client. ACL tokens are sent as a
/// header and are never included in exceptions, messages, or <see cref="ToString"/> output.
/// </summary>
public sealed class HttpConsulKvClient : IConsulKvClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseAddress;
    private readonly string? _token;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Creates an HTTP Consul KV client.</summary>
    /// <param name="httpClient">A caller-owned HTTP client.</param>
    /// <param name="baseAddress">The Consul agent address, for example <c>http://127.0.0.1:8500</c>.</param>
    /// <param name="token">An optional ACL token. It is stored opaquely and never logged.</param>
    public HttpConsulKvClient(HttpClient httpClient, string baseAddress, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAddress);
        if (!string.IsNullOrWhiteSpace(token))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(token);
        }

        _httpClient = httpClient;
        _baseAddress = baseAddress.TrimEnd('/');
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
    }

    /// <summary>Returns a redacted description that never contains the ACL token.</summary>
    public override string ToString() => $"consul:{_baseAddress}";

    /// <inheritdoc />
    public async Task<ConsulKvListResult> ListAsync(
        string prefix,
        ConsulKvListOptions? options,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var query = BuildQuery(options, recurse: true);
        var response = await SendAsync(
                HttpMethod.Get,
                $"/v1/kv/{Uri.EscapeDataString(NormalizeKey(prefix))}{query}",
                content: null,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadListResultAsync(response).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ConsulKvListResult> GetAsync(
        string key,
        ConsulKvListOptions? options,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var query = BuildQuery(options, recurse: false);
        var response = await SendAsync(
                HttpMethod.Get,
                $"/v1/kv/{Uri.EscapeDataString(NormalizeKey(key))}{query}",
                content: null,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadListResultAsync(response).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(bool Applied, ulong NewIndex)> PutAsync(
        string key,
        ReadOnlyMemory<byte> value,
        ulong? cas,
        ConsulKvWriteOptions? options,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var query = BuildWriteQuery(options, cas);
        using var content = new ByteArrayContent(value.ToArray());
        var response = await SendAsync(
                HttpMethod.Put,
                $"/v1/kv/{Uri.EscapeDataString(NormalizeKey(key))}{query}",
                content,
                cancellationToken
            )
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
#if NETSTANDARD2_0
        var body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
#else
        var body = (
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
        ).Trim();
#endif
        var applied = string.Equals(body, "true", StringComparison.OrdinalIgnoreCase);
        return (applied, ReadIndexHeader(response));
    }

    /// <inheritdoc />
    public async Task<(bool Applied, ulong NewIndex)> DeleteAsync(
        string key,
        ulong? cas,
        ConsulKvWriteOptions? options,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var query = BuildWriteQuery(options, cas);
        var response = await SendAsync(
                HttpMethod.Delete,
                $"/v1/kv/{Uri.EscapeDataString(NormalizeKey(key))}{query}",
                content: null,
                cancellationToken
            )
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
#if NETSTANDARD2_0
        var deleteBody = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
#else
        var deleteBody = (
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
        ).Trim();
#endif
        var deleteApplied = string.Equals(deleteBody, "true", StringComparison.OrdinalIgnoreCase);
        return (deleteApplied, ReadIndexHeader(response));
    }

    /// <inheritdoc />
    public async Task<ConsulTxnResult> TransactAsync(
        IReadOnlyList<ConsulTxnOperation> operations,
        ConsulKvWriteOptions? options,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
        {
            throw new ArgumentException(
                "At least one transaction operation is required.",
                nameof(operations)
            );
        }

        if (operations.Count > 64)
        {
            throw new ArgumentException(
                "Consul transactions support at most 64 operations.",
                nameof(operations)
            );
        }

        var payload = operations
            .Select(static operation =>
            {
                if (string.Equals(operation.Verb, "delete", StringComparison.OrdinalIgnoreCase))
                {
                    return new Dictionary<string, object?>
                    {
                        ["KV"] = new Dictionary<string, object?>
                        {
                            ["Verb"] = "delete",
                            ["Key"] = operation.Key,
                            ["Index"] = operation.Cas is { } deleteCas ? deleteCas : 0UL,
                        },
                    };
                }

                return new Dictionary<string, object?>
                {
                    ["KV"] = new Dictionary<string, object?>
                    {
                        ["Verb"] = "set",
                        ["Key"] = operation.Key,
                        ["Value"] = Convert.ToBase64String(operation.Value ?? Array.Empty<byte>()),
                        ["Index"] = operation.Cas is { } setCas ? setCas : 0UL,
                    },
                };
            })
            .ToArray();
        var query = BuildTxnQuery(options);
        using var content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            System.Text.Encoding.UTF8,
            "application/json"
        );
        var response = await SendAsync(
                HttpMethod.Put,
                $"/v1/txn{query}",
                content,
                cancellationToken
            )
            .ConfigureAwait(false);
        var index = ReadIndexHeader(response);
#if NETSTANDARD2_0
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#else
        var body = await response
            .Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
#endif
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return new ConsulTxnResult(true, index);
        }

        string[]? errors = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (
                document.RootElement.TryGetProperty("Errors", out var errorsElement)
                && errorsElement.ValueKind == JsonValueKind.Array
            )
            {
                errors = errorsElement
                    .EnumerateArray()
                    .Select(static element =>
                        element.TryGetProperty("What", out var what)
                            ? (what.GetString() ?? "transaction failed")
                            : "transaction failed"
                    )
                    .ToArray();
            }
        }
        catch (JsonException)
        {
            errors = null;
        }

        return new ConsulTxnResult(false, index, errors);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string pathAndQuery,
        HttpContent? content,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(method, _baseAddress + pathAndQuery);
        if (content is not null)
        {
            request.Content = content;
        }

        if (_token is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Consul-Token", _token);
        }

        try
        {
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new HttpRequestException(
                $"The Consul request to '{_baseAddress}' failed.",
                exception
            );
        }
    }

    private static async Task<ConsulKvListResult> ReadListResultAsync(HttpResponseMessage response)
    {
        var index = ReadIndexHeader(response);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ConsulKvListResult([], index);
        }

        response.EnsureSuccessStatusCode();
#if NETSTANDARD2_0
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#else
        var body = await response
            .Content.ReadAsStringAsync(CancellationToken.None)
            .ConfigureAwait(false);
#endif
        if (
            string.IsNullOrWhiteSpace(body)
            || string.Equals(body.Trim(), "null", StringComparison.Ordinal)
        )
        {
            return new ConsulKvListResult([], index);
        }

        List<ConsulJsonEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<ConsulJsonEntry>>(body, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Consul KV response was malformed.", exception);
        }

        if (entries is null)
        {
            return new ConsulKvListResult([], index);
        }

        var result = new List<ConsulKvEntry>(entries.Count);
        foreach (var entry in entries)
        {
            byte[]? value = null;
            if (entry.Value is not null)
            {
                try
                {
                    value = Convert.FromBase64String(entry.Value);
                }
                catch (FormatException exception)
                {
                    throw new InvalidDataException(
                        $"The Consul key '{entry.Key}' has a malformed base64 value.",
                        exception
                    );
                }
            }

            result.Add(
                new ConsulKvEntry(
                    entry.Key ?? string.Empty,
                    value,
                    entry.ModifyIndex,
                    entry.CreateIndex
                )
            );
        }

        return new ConsulKvListResult(result, index);
    }

    private static string BuildQuery(ConsulKvListOptions? options, bool recurse)
    {
        var parts = new List<string>();
        if (recurse)
        {
            parts.Add("recurse=");
        }

        if (options is null)
        {
            return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
        }

        if (!string.IsNullOrWhiteSpace(options.Datacenter))
        {
            parts.Add("dc=" + Uri.EscapeDataString(options.Datacenter));
        }

        if (!string.IsNullOrWhiteSpace(options.Namespace))
        {
            parts.Add("ns=" + Uri.EscapeDataString(options.Namespace));
        }

        if (!string.IsNullOrWhiteSpace(options.Partition))
        {
            parts.Add("partition=" + Uri.EscapeDataString(options.Partition));
        }

        if (options.Consistency == ConsulConsistencyMode.Stale)
        {
            parts.Add("stale=");
        }
        else if (options.Consistency == ConsulConsistencyMode.Consistent)
        {
            parts.Add("consistent=");
        }

        if (options.WaitIndex is { } waitIndex)
        {
            parts.Add("index=" + waitIndex.ToString(CultureInfo.InvariantCulture));
        }

        if (options.WaitTimeout is { } wait)
        {
            parts.Add("wait=" + FormatWaitTimeout(wait));
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    private static string BuildWriteQuery(ConsulKvWriteOptions? options, ulong? cas)
    {
        var parts = new List<string>();
        if (options is not null)
        {
            if (!string.IsNullOrWhiteSpace(options.Datacenter))
            {
                parts.Add("dc=" + Uri.EscapeDataString(options.Datacenter));
            }

            if (!string.IsNullOrWhiteSpace(options.Namespace))
            {
                parts.Add("ns=" + Uri.EscapeDataString(options.Namespace));
            }

            if (!string.IsNullOrWhiteSpace(options.Partition))
            {
                parts.Add("partition=" + Uri.EscapeDataString(options.Partition));
            }
        }

        if (cas is { } casValue)
        {
            parts.Add("cas=" + casValue.ToString(CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    private static string BuildTxnQuery(ConsulKvWriteOptions? options)
    {
        var parts = new List<string>();
        if (options is not null)
        {
            if (!string.IsNullOrWhiteSpace(options.Datacenter))
            {
                parts.Add("dc=" + Uri.EscapeDataString(options.Datacenter));
            }

            if (!string.IsNullOrWhiteSpace(options.Namespace))
            {
                parts.Add("ns=" + Uri.EscapeDataString(options.Namespace));
            }

            if (!string.IsNullOrWhiteSpace(options.Partition))
            {
                parts.Add("partition=" + Uri.EscapeDataString(options.Partition));
            }
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    private static string FormatWaitTimeout(TimeSpan wait)
    {
        if (wait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(wait),
                "A blocking wait must be positive."
            );
        }

        var totalSeconds = (long)wait.TotalSeconds;
        if (totalSeconds >= 1)
        {
            return totalSeconds.ToString(CultureInfo.InvariantCulture) + "s";
        }

        var milliseconds = Math.Max(1L, (long)wait.TotalMilliseconds);
        return milliseconds.ToString(CultureInfo.InvariantCulture) + "ms";
    }

    private static ulong ReadIndexHeader(HttpResponseMessage response)
    {
        if (
            response.Headers.TryGetValues("X-Consul-Index", out var values)
            && ulong.TryParse(
                values.FirstOrDefault(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index
            )
        )
        {
            return index;
        }

        return 0;
    }

    private static string NormalizeKey(string key) => key.Trim('/');

    private sealed class ConsulJsonEntry
    {
        [System.Text.Json.Serialization.JsonConstructor]
        public ConsulJsonEntry(string? key, string? value, ulong modifyIndex, ulong createIndex)
        {
            Key = key;
            Value = value;
            ModifyIndex = modifyIndex;
            CreateIndex = createIndex;
        }

        public string? Key { get; }

        public string? Value { get; }

        public ulong ModifyIndex { get; }

        public ulong CreateIndex { get; }
    }
}
