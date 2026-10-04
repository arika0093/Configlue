using Configlue.Sources;

namespace Configlue.Source.Ssm;

/// <summary>Registers Parameter Store hierarchy sources.</summary>
public static class SsmParameterStoreSourceRegistration
{
    /// <summary>Adds a Parameter Store source. Supplied clients remain externally owned.</summary>
    public static ConfiglueSourceRegistration FromSsmParameterStore(
        this ConfiglueSourceSetBuilder sources,
        SsmParameterStoreSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPath);
        options.ResourceOptions?.Validate();
        if (
            (options.Client is null ? 0 : 1)
                + (options.ClientFactory is null ? 0 : 1)
                + (options.ClientResolver is null ? 0 : 1)
            != 1
        )
        {
            throw new ArgumentException(
                "Configure exactly one of Client, ClientFactory, or ClientResolver.",
                nameof(options)
            );
        }

        if (options.Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new SsmParameterStoreSourceDefinition(options)
        );
    }

    private sealed class SsmParameterStoreSourceDefinition(SsmParameterStoreSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            var resourceOptions = options.ResourceOptions ?? new SsmParameterStoreOptions();
            var fixedClient = options.Client;
            if (options.ClientFactory is { } factory)
            {
                fixedClient =
                    factory(context.Services)
                    ?? throw new InvalidOperationException("The SSM client factory returned null.");
            }

            var rootPath = SsmParameterPath.NormalizeRootPath(options.RootPath);
            SsmParameterStoreSource<TFragment> source;
            if (options.ClientResolver is { } resolver)
            {
                var services = context.Services;
                source = new SsmParameterStoreSource<TFragment>(
                    new SsmAwsParameterClient(
                        resolver(services, RouteKey.Default)
                            ?? throw new InvalidOperationException(
                                "The SSM client resolver returned null."
                            )
                    ),
                    rootPath,
                    context.ModelSchema,
                    resourceOptions,
                    clientSelector: resourceContext => new SsmAwsParameterClient(
                        resolver(services, resourceContext.Route)
                            ?? throw new InvalidOperationException(
                                "The SSM client resolver returned null."
                            )
                    ),
                    writable: options.Writable,
                    watchChanges: options.WatchChanges
                );
            }
            else
            {
                source = new SsmParameterStoreSource<TFragment>(
                    fixedClient!,
                    rootPath,
                    context.ModelSchema,
                    resourceOptions,
                    writable: options.Writable,
                    watchChanges: options.WatchChanges
                );
            }

            context.Own(source);
            var physicalOrigin = $"ssm:{rootPath}";
            var stateSource = ConfiglueSourceCompletion.WithDescribedIdentity(
                source,
                options.Id,
                string.Join(
                    "\n",
                    rootPath,
                    resourceOptions.Recursive.ToString(),
                    resourceOptions.WithDecryption.ToString(),
                    options.Writable.ToString(),
                    options.WatchChanges.ToString()
                ),
                options.Priority,
                options.FallbackCondition,
                physicalOrigin,
                resourceOptions.FixedResourceId
            );
            return context.Complete(stateSource);
        }
    }
}
