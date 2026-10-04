using Configlue.Codecs;
using Configlue.Sources;

namespace Configlue.Resource.AzureKeyVault;

/// <summary>Registers Key Vault-backed sources.</summary>
/// <remarks>
/// <para>Two modes:</para>
/// <list type="bullet">
/// <item>Explicit member mapping (<see cref="FromKeyVaultSecrets"/>): each leaf member maps to one secret.</item>
/// <item>Single-secret document (<see cref="FromKeyVaultSecret"/>): one secret holds a serialized fragment via the Resource + Codec pipeline.</item>
/// </list>
/// </remarks>
public static class KeyVaultSourceRegistration
{
    /// <summary>Adds a Key Vault secrets source with explicit member mappings.</summary>
    public static ConfiglueSourceRegistration FromKeyVaultSecrets(
        this ConfiglueSourceSetBuilder sources,
        KeyVaultSecretsOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.VaultUri);
        ValidateClientConfiguration(options.Client, options.ClientFactory, options.Credential);
        if (options.Mappings.Count == 0 && !options.EnableConventionMapping)
        {
            throw new ArgumentException(
                "Configure at least one explicit Key Vault secret mapping or enable convention mapping.",
                nameof(options)
            );
        }

        foreach (var mapping in options.Mappings)
        {
            ArgumentNullException.ThrowIfNull(mapping);
        }

        if (options.ConventionPrefix is not null)
        {
            KeyVaultSecretName.Validate(options.ConventionPrefix);
        }

        if (options.FixedVersion is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.FixedVersion);
        }

        if (options.PollInterval is { } pollInterval && pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The polling interval must be greater than zero."
            );
        }

        if (options.Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new KeyVaultSecretsDefinition(options)
        );
    }

    /// <summary>Adds a Key Vault single-secret document source through the Resource + Codec pipeline.</summary>
    public static ConfiglueSourceRegistration FromKeyVaultSecret(
        this ConfiglueSourceSetBuilder sources,
        KeyVaultSecretSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.VaultUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SecretName);
        KeyVaultSecretName.Validate(options.SecretName);
        if (options.SecretVersion is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.SecretVersion);
        }

        ValidateClientConfiguration(options.Client, options.ClientFactory, options.Credential);
        ArgumentNullException.ThrowIfNull(options.Codec);
        if (
            options.PollInterval is { } documentPollInterval
            && documentPollInterval <= TimeSpan.Zero
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The polling interval must be greater than zero."
            );
        }

        if (options.Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new KeyVaultSecretDocumentDefinition(options)
        );
    }

    private static void ValidateClientConfiguration(
        IKeyVaultSecretClient? client,
        Func<IServiceProvider?, IKeyVaultSecretClient>? factory,
        object? credential
    )
    {
        if (client is not null && factory is not null)
        {
            throw new ArgumentException("Configure exactly one of Client or ClientFactory.");
        }

        if (client is not null && credential is not null)
        {
            throw new ArgumentException(
                "Credential is unused when Client is supplied. Configure exactly one of Client, ClientFactory, or Credential."
            );
        }

        if (factory is not null && credential is not null)
        {
            throw new ArgumentException(
                "Credential is unused when ClientFactory is supplied. Configure exactly one of Client, ClientFactory, or Credential."
            );
        }
    }

    private static IKeyVaultSecretClient ResolveClient(
        IKeyVaultSecretClient? client,
        Func<IServiceProvider?, IKeyVaultSecretClient>? factory,
        Azure.Core.TokenCredential? credential,
        Uri vaultUri,
        IServiceProvider? services
    )
    {
        if (client is not null)
        {
            return client;
        }

        if (factory is not null)
        {
            return factory(services)
                ?? throw new InvalidOperationException(
                    "The Key Vault client factory returned null."
                );
        }

        return KeyVaultClients.Create(vaultUri, credential);
    }

    private sealed class KeyVaultSecretsDefinition(KeyVaultSecretsOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            var client = ResolveClient(
                options.Client,
                options.ClientFactory,
                options.Credential,
                options.VaultUri,
                context.Services
            );
            var mappings = KeyVaultSecretsState<TFragment>.ResolveMappings(
                context.ModelSchema,
                options.Mappings,
                options.EnableConventionMapping,
                options.ConventionPrefix,
                options.FixedVersion
            );
            var state = new KeyVaultSecretsState<TFragment>(
                client,
                options.VaultUri,
                context.ModelSchema,
                mappings,
                options.Writable,
                options.PollInterval,
                options.ValueParser,
                options.JsonSerializerOptions
            );
            context.Own(state);
            var physicalOrigin = state.PhysicalOrigin;
            StateSource<TFragment> source;
            if (options.Id is { } id)
            {
                source = new StateSource<TFragment>(
                    id,
                    state,
                    new StateSourceOptions<TFragment>
                    {
                        Priority = options.Priority,
                        FallbackCondition = options.FallbackCondition,
                        PhysicalOrigin = physicalOrigin,
                        FixedResourceId = options.FixedResourceId,
                        DisableWriteCapability = !options.Writable,
                    }
                );
            }
            else
            {
                source = new StateSource<TFragment>(
                    state,
                    new StateSourceOptions<TFragment>
                    {
                        Priority = options.Priority,
                        FallbackCondition = options.FallbackCondition,
                        PhysicalOrigin = physicalOrigin,
                        FixedResourceId = options.FixedResourceId,
                        DisableWriteCapability = !options.Writable,
                        LogicalDescriptor = string.Join(
                            "\n",
                            options.VaultUri.Host,
                            string.Join(",", mappings.Select(static m => m.SecretName))
                        ),
                    }
                );
            }

            return context.Complete(source);
        }
    }

    private sealed class KeyVaultSecretDocumentDefinition(KeyVaultSecretSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            var client = ResolveClient(
                options.Client,
                options.ClientFactory,
                options.Credential,
                options.VaultUri,
                context.Services
            );
            var resource = new KeyVaultSecretResource(
                client,
                options.VaultUri,
                options.SecretName,
                options.SecretVersion,
                options.ResourceOptions
            );
            context.Own(resource);

            KeyVaultPollingWatcher? watcher = null;
            if (options.PollInterval is { } pollInterval && options.SecretVersion is null)
            {
                watcher = new KeyVaultPollingWatcher(
                    ct => GetDocumentRevisionAsync(client, options.SecretName, ct),
                    pollInterval
                );
                context.Own(watcher);
            }

            var serialized = new SerializedSource<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                writer: options.Writable ? resource : null,
                watcher: watcher
            );
            var physicalOrigin = KeyVaultClients.GetPhysicalOrigin(options.VaultUri);
            StateSource<TFragment> source;
            if (options.Id is { } id)
            {
                source = new StateSource<TFragment>(
                    id,
                    serialized,
                    new StateSourceOptions<TFragment>
                    {
                        Priority = options.Priority,
                        FallbackCondition = options.FallbackCondition,
                        PhysicalOrigin = physicalOrigin,
                        FixedResourceId = options.ResourceOptions?.FixedResourceId,
                        DisableWriteCapability = !options.Writable,
                    }
                );
            }
            else
            {
                source = new StateSource<TFragment>(
                    serialized,
                    new StateSourceOptions<TFragment>
                    {
                        Priority = options.Priority,
                        FallbackCondition = options.FallbackCondition,
                        PhysicalOrigin = physicalOrigin,
                        FixedResourceId = options.ResourceOptions?.FixedResourceId,
                        DisableWriteCapability = !options.Writable,
                        LogicalDescriptor = string.Join(
                            "\n",
                            options.VaultUri.Host,
                            options.SecretName,
                            options.SecretVersion ?? "<current>"
                        ),
                    }
                );
            }

            return context.Complete(source);
        }

        private static async ValueTask<string?> GetDocumentRevisionAsync(
            IKeyVaultSecretClient client,
            string secretName,
            CancellationToken cancellationToken
        )
        {
            try
            {
                var secret = await client
                    .GetSecretAsync(secretName, null, cancellationToken)
                    .ConfigureAwait(false);
                if (!secret.Metadata.Enabled)
                {
                    return null;
                }

                return KeyVaultSecretResource.RevisionForVersion(secret.Metadata.Version);
            }
            catch (KeyVaultSecretNotFoundException)
            {
                return null;
            }
        }
    }
}
