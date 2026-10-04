using Azure.Core;
using Azure.Data.AppConfiguration;
using Configlue.Sources;

namespace Configlue.Resource.AzureAppConfiguration;

/// <summary>Registers Azure App Configuration sources.</summary>
public static class AzureAppConfigurationSourceRegistration
{
    /// <summary>
    /// Adds an Azure App Configuration source. Supplied clients and credentials remain externally owned.
    /// Azure identity is the documented default; connection strings are allowed but never logged.
    /// </summary>
    public static ConfiglueSourceRegistration FromAzureAppConfiguration(
        this ConfiglueSourceSetBuilder sources,
        AzureAppConfigurationSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new AzureAppConfigurationSourceDefinition(options)
        );
    }

    private sealed class AzureAppConfigurationSourceDefinition(
        AzureAppConfigurationSourceOptions options
    ) : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            var source = CreateSourceCore<TFragment>(context.ModelSchema, context.Services);
            context.Own(source);
            var physicalOrigin = DescribePhysicalOrigin(options);
            return context.Complete(
                ConfiglueSourceCompletion.WithDescribedIdentity(
                    source,
                    options.Id,
                    string.Join(
                        "\n",
                        options.KeyFilter,
                        options.LabelFilter ?? "<unlabeled>",
                        options.TrimKeyPrefix ?? string.Empty,
                        options.SnapshotName ?? string.Empty,
                        options.SentinelKey ?? string.Empty
                    ),
                    options.Priority,
                    options.FallbackCondition,
                    physicalOrigin,
                    options.FixedResourceId
                )
            );
        }

        private AzureAppConfigurationSource<TFragment> CreateSourceCore<TFragment>(
            ConfiglueModelSchema modelSchema,
            IServiceProvider? serviceProvider
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            var client = ResolveClient(serviceProvider);
            return new AzureAppConfigurationSource<TFragment>(client, modelSchema, options);
        }

        private IAppConfigurationClient ResolveClient(IServiceProvider? serviceProvider)
        {
            if (options.Client is { } direct)
            {
                return new AzureAppConfigurationClientAdapter(direct);
            }

            if (options.ClientFactory is { } clientFactory)
            {
                return new AzureAppConfigurationClientAdapter(
                    clientFactory(serviceProvider)
                        ?? throw new InvalidOperationException(
                            "The App Configuration client factory returned null."
                        )
                );
            }

            if (options.Endpoint is { } endpoint)
            {
                var credential =
                    options.Credential
                    ?? options.CredentialFactory!(serviceProvider)
                    ?? throw new InvalidOperationException(
                        "The App Configuration credential factory returned null."
                    );
                return new AzureAppConfigurationClientAdapter(
                    new ConfigurationClient(endpoint, credential)
                );
            }

            return new AzureAppConfigurationClientAdapter(
                new ConfigurationClient(options.ConnectionString)
            );
        }

        private static string DescribePhysicalOrigin(
            AzureAppConfigurationSourceOptions sourceOptions
        )
        {
            string host;
            if (sourceOptions.Endpoint is { } endpoint)
            {
                host = endpoint.Host;
            }
            else if (sourceOptions.ConnectionString is { } connectionString)
            {
                host = "connection-string";
                foreach (var part in connectionString.Split(';'))
                {
                    var trimmed = part.Trim();
                    if (
                        trimmed.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase)
                        && Uri.TryCreate(
                            trimmed["Endpoint=".Length..].Trim(),
                            UriKind.Absolute,
                            out var uri
                        )
                    )
                    {
                        host = uri.Host;
                        break;
                    }
                }
            }
            else
            {
                host = "injected-client";
            }

            var snapshot = sourceOptions.SnapshotName is { } name
                ? $" snapshot:{name}"
                : string.Empty;
            return $"appconfig:{host} filter:{sourceOptions.KeyFilter} label:{sourceOptions.LabelFilter ?? "<unlabeled>"}{snapshot}";
        }
    }
}
