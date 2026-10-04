using System.Net.Http;
using System.Text;

namespace Configlue.Resource.Kubernetes;

/// <summary>
/// Cluster connection settings used to build an <see cref="HttpClient"/> for
/// <see cref="HttpKubernetesObjectClient"/>. Bearer tokens and certificates are never
/// included in <see cref="ToString"/> output or exception messages.
/// </summary>
public sealed class KubernetesConfiguration
{
    /// <summary>Creates cluster connection settings.</summary>
    public KubernetesConfiguration(string server, string? defaultNamespace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        Server = server.TrimEnd('/');
        DefaultNamespace = string.IsNullOrWhiteSpace(defaultNamespace) ? null : defaultNamespace;
    }

    /// <summary>The API server base address.</summary>
    public string Server { get; }

    /// <summary>The default namespace when sources do not specify one.</summary>
    public string? DefaultNamespace { get; init; }

    /// <summary>The bearer token. Never logged.</summary>
    public string? Token { get; init; }

    /// <summary>The PEM-encoded cluster CA bundle. Never logged.</summary>
    public string? CaCertPem { get; init; }

    /// <summary>Whether to skip TLS verification (testing only).</summary>
    public bool InsecureSkipTlsVerify { get; init; }

    /// <summary>Whether the current process runs inside a Kubernetes cluster.</summary>
    public static bool IsInCluster =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"))
        && !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT")
        );

    /// <summary>Loads standard in-cluster settings from the service-account projection.</summary>
    /// <exception cref="InvalidOperationException">The process is not running in a cluster.</exception>
    public static KubernetesConfiguration ForInCluster(string? defaultNamespace = null)
    {
        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(port))
        {
            throw new InvalidOperationException(
                "The process is not running inside a Kubernetes cluster."
            );
        }

        const string tokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
        const string caPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";
        const string namespacePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";
        var configuration = new KubernetesConfiguration(
            $"https://{host}:{port}",
            defaultNamespace ?? TryReadText(namespacePath)
        )
        {
            Token = TryReadText(tokenPath),
            CaCertPem = TryReadText(caPath),
        };
        return configuration;
    }

    /// <summary>
    /// Loads out-of-cluster settings from a kubeconfig file (the KUBECONFIG environment variable
    /// or ~/.kube/config). Supports server, namespace, bearer tokens, and base64 CA data for the
    /// current context. Client-certificate authentication must be supplied by injecting a
    /// pre-configured <see cref="HttpClient"/> instead.
    /// </summary>
    public static KubernetesConfiguration FromKubeConfigFile(
        string? path = null,
        string? contextName = null,
        string? defaultNamespace = null
    )
    {
        var resolved =
            path
            ?? Environment
                .GetEnvironmentVariable("KUBECONFIG")
                ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)[0]
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".kube",
                "config"
            );
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException(
                $"The kubeconfig file '{resolved}' was not found.",
                resolved
            );
        }

        var lines = File.ReadAllLines(resolved, Encoding.UTF8);
        var currentContext = FindScalar(lines, "current-context") ?? contextName;
        var targetContext = contextName ?? currentContext;
        var contextBlock = FindMappingBlock(lines, "contexts", targetContext);
        var clusterName = FindScalar(contextBlock, "cluster");
        var namespaceName = defaultNamespace ?? FindScalar(contextBlock, "namespace");
        var userName = FindScalar(contextBlock, "user");
        var clusterBlock = FindMappingBlock(lines, "clusters", clusterName);
        var userBlock = FindMappingBlock(lines, "users", userName);
        var server =
            FindScalar(clusterBlock, "server")
            ?? throw new InvalidDataException(
                "The kubeconfig file does not declare a cluster server."
            );
        var caData = FindScalar(clusterBlock, "certificate-authority-data");
        var token = FindScalar(userBlock, "token");
        string? caPem = null;
        if (caData is not null && !string.IsNullOrWhiteSpace(caData))
        {
            var raw = Convert.FromBase64String(caData.Trim());
            caPem = Encoding.UTF8.GetString(raw);
        }

        return new KubernetesConfiguration(server, namespaceName)
        {
            Token = token is not null && !string.IsNullOrWhiteSpace(token) ? token.Trim() : null,
            CaCertPem = caPem,
        };
    }

    /// <summary>Builds a cluster-targeted HTTP client. The caller owns the returned client.</summary>
    public HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler();
        if (InsecureSkipTlsVerify)
        {
            // Explicit opt-in for local testing only; production clusters verify TLS.
#pragma warning disable S4830 // Controlled by InsecureSkipTlsVerify.
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
#pragma warning restore S4830
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(Server, UriKind.Absolute),
        };
        if (!string.IsNullOrWhiteSpace(Token))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        }

        return client;
    }

    /// <summary>Builds a shared object client over a caller-owned HTTP client.</summary>
    /// <param name="httpClient">The configured HTTP client. It remains caller-owned.</param>
    public static IKubernetesObjectClient CreateObjectClient(HttpClient httpClient) =>
        new HttpKubernetesObjectClient(httpClient);

    /// <inheritdoc />
    /// <remarks>Never includes bearer tokens or certificates.</remarks>
    public override string ToString() =>
        $"k8s:{Server} namespace={DefaultNamespace ?? "<none>"} auth={(string.IsNullOrWhiteSpace(Token) ? "none" : "bearer")}";

    private static string? TryReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindScalar(string[] lines, string key)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key + ":", StringComparison.Ordinal))
            {
                var value = trimmed.Substring(key.Length + 1).Trim().Trim('"', '\'');
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static string[] FindMappingBlock(string[] lines, string section, string? entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return [];
        }

        var inSection = false;
        var capturing = false;
        var indent = int.MaxValue;
        var block = new List<string>();
        foreach (var line in lines)
        {
            if (
                line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
                && line.Trim().Contains(entryName, StringComparison.Ordinal)
            )
            {
                capturing = inSection;
                indent = line.IndexOf('-');
                if (capturing)
                {
                    block.Add(line);
                }

                continue;
            }

            if (line.Trim().Equals(section + ":", StringComparison.Ordinal))
            {
                inSection = true;
                continue;
            }

            if (
                inSection
                && line.TrimEnd().EndsWith(':')
                && line.TrimStart().Length == line.Trim().Length + 0
                && !line.StartsWith(' ')
                && !line.StartsWith('-')
            )
            {
                break;
            }

            if (capturing)
            {
                if (
                    line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(line)
                )
                {
                    if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                    {
                        break;
                    }

                    continue;
                }

                block.Add(line);
                _ = indent;
            }
        }

        return block.ToArray();
    }
}
