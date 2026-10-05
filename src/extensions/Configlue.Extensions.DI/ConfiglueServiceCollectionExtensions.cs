using Configlue.CompilerServices;
using Configlue.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue;

/// <summary>Registers generated Configlue state with dependency injection.</summary>
/// <remarks>
/// The canonical composition path is <see cref="AddConfiglue(IServiceCollection, Action{ConfiglueBuilder})"/>:
/// models are registered once on <see cref="ConfiglueBuilder"/> and translated to DI descriptors by a
/// single code path. Named/profile/per-subject state is represented in model registration
/// (<see cref="ConfiglueModelBuilder{TModel}.StateName"/>, <c>EnableProfiles</c>/<c>EnableDynamicStates</c>,
/// <c>PerSubject{TAccessor}</c>) rather than parallel registration APIs.
/// </remarks>
public static class ConfiglueServiceCollectionExtensions
{
    /// <summary>Registers the same model definitions used by non-DI Configlue contexts.</summary>
    public static IServiceCollection AddConfiglue(
        this IServiceCollection services,
        Action<ConfiglueBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ConfiglueBuilder();
        configure(builder);
        return services.AddConfiglueBuilder(builder);
    }

    /// <summary>Registers definitions already collected by a Configlue builder.</summary>
    public static IServiceCollection AddConfiglueBuilder(
        this IServiceCollection services,
        ConfiglueBuilder builder
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(builder);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ConfiglueContext)))
        {
            throw new InvalidOperationException("A Configlue context is already registered.");
        }

        builder.Seal();
        services.AddSingleton<ConfiglueContext>(provider => builder.CreateContext(provider));
        var visitor = new ConfiglueFacadeRegistrationVisitor(services, builder.HostPaths);
        foreach (var registration in builder.Registrations)
        {
            registration.Accept(visitor);
        }

        return services;
    }

    /// <summary>Translates one model registration to DI descriptors in a single code path.</summary>
    private sealed class ConfiglueFacadeRegistrationVisitor(
        IServiceCollection services,
        IConfiglueHostPaths hostPaths
    ) : IConfiglueRegistrationVisitor
    {
        public void Visit<TModel>(ConfiglueModelRegistration<TModel> registration)
            where TModel : IConfiglueFacadeModel<TModel>
        {
            if (registration.EnableDynamicStates)
            {
                services.AddSingleton<IConfiglueStateRegistry<TModel>>(provider =>
                    provider.GetRequiredService<ConfiglueContext>().GetStateRegistry<TModel>()
                );
            }
            if (registration.EnableProfiles)
            {
                services.AddSingleton<IConfiglueProfiledState<TModel>>(provider =>
                    provider.GetRequiredService<ConfiglueContext>().GetProfiledState<TModel>()
                );
            }
            if (registration.StateName.Length != 0 && typeof(TModel).IsClass)
            {
                services.AddSingleton(new ConfiglueNamedState<TModel>(registration.StateName));
            }

            if (registration.RuntimeLifetime == RuntimeLifetimeRequirement.Scoped)
            {
                RegisterScoped(registration);
            }
            else
            {
                RegisterShared(registration);
            }
        }

        private void RegisterShared<TModel>(ConfiglueModelRegistration<TModel> registration)
            where TModel : IConfiglueFacadeModel<TModel>
        {
            var keyed = registration.StateName.Length != 0;
            var stateName = registration.StateName;
            if (!registration.IsPerSubject)
            {
                AddShared<IReadOnlyState<TModel>>(
                    keyed,
                    stateName,
                    (provider, _) =>
                        provider.GetRequiredService<ConfiglueContext>().GetState<TModel>(stateName)
                );
                AddShared<IWritableState<TModel>>(
                    keyed,
                    stateName,
                    (provider, _) =>
                        provider.GetRequiredService<ConfiglueContext>().GetState<TModel>(stateName)
                );
                AddShared<ISubjectState<TModel>>(
                    keyed,
                    stateName,
                    (provider, _) =>
                        provider
                            .GetRequiredService<ConfiglueContext>()
                            .GetSubjectState<TModel>(stateName)
                );
                // Capability aliases resolve through the shared runtime so there is exactly
                // one state instance per (TModel, StateName).
                AddShared<IConfiglueEditSessions<TModel>>(
                    keyed,
                    stateName,
                    (provider, _) =>
                        (IConfiglueEditSessions<TModel>)
                            ResolveSharedRuntime<TModel>(provider, keyed, stateName)
                );
                AddShared<IConfiglueDiagnostics<TModel>>(
                    keyed,
                    stateName,
                    (provider, _) =>
                        (IConfiglueDiagnostics<TModel>)
                            ResolveSharedRuntime<TModel>(provider, keyed, stateName)
                );
                AddShared<IConfiglueSources<TModel>>(
                    keyed,
                    stateName,
                    (provider, _) =>
                        (IConfiglueSources<TModel>)
                            ResolveSharedRuntime<TModel>(provider, keyed, stateName)
                );
                return;
            }

            // Per-subject edit sessions and diagnostics checks must resolve through the
            // scoped CurrentSubjectState. Keep only subject-agnostic facades as shared
            // singletons so the raw runtime descriptor cannot overwrite the scoped fix.
            var subjectAccessorType = registration.SubjectAccessorType!;
            if (!keyed)
            {
                services.AddScoped(provider => new CurrentSubjectState<TModel>(
                    provider
                        .GetRequiredService<ConfiglueContext>()
                        .GetSubjectState<TModel>(stateName),
                    (IConfiglueSubjectAccessor)provider.GetRequiredService(subjectAccessorType)
                ));
                services.AddScoped<IReadOnlyState<TModel>>(provider =>
                    provider.GetRequiredService<CurrentSubjectState<TModel>>()
                );
                services.AddScoped<IWritableState<TModel>>(provider =>
                    provider.GetRequiredService<CurrentSubjectState<TModel>>()
                );
                services.AddScoped<IConfiglueDiagnostics<TModel>>(provider =>
                    provider.GetRequiredService<CurrentSubjectState<TModel>>()
                );
                services.AddScoped<IConfiglueEditSessions<TModel>>(provider =>
                    provider.GetRequiredService<CurrentSubjectState<TModel>>()
                );
            }
            else
            {
                services.AddKeyedScoped<CurrentSubjectState<TModel>>(
                    stateName,
                    (provider, _) =>
                        new CurrentSubjectState<TModel>(
                            provider
                                .GetRequiredService<ConfiglueContext>()
                                .GetSubjectState<TModel>(stateName),
                            (IConfiglueSubjectAccessor)
                                provider.GetRequiredService(subjectAccessorType)
                        )
                );
                services.AddKeyedScoped<IReadOnlyState<TModel>>(
                    stateName,
                    (provider, key) =>
                        provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                );
                services.AddKeyedScoped<IWritableState<TModel>>(
                    stateName,
                    (provider, key) =>
                        provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                );
                services.AddKeyedScoped<IConfiglueDiagnostics<TModel>>(
                    stateName,
                    (provider, key) =>
                        provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                );
                services.AddKeyedScoped<IConfiglueEditSessions<TModel>>(
                    stateName,
                    (provider, key) =>
                        provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                );
            }
            AddShared<ISubjectState<TModel>>(
                keyed,
                stateName,
                (provider, _) =>
                    provider
                        .GetRequiredService<ConfiglueContext>()
                        .GetSubjectState<TModel>(stateName)
            );
            AddShared<IConfiglueSources<TModel>>(
                keyed,
                stateName,
                (provider, _) =>
                    (IConfiglueSources<TModel>)
                        provider.GetRequiredService<ConfiglueContext>().GetState<TModel>(stateName)
            );
        }

        private void RegisterScoped<TModel>(ConfiglueModelRegistration<TModel> registration)
            where TModel : IConfiglueFacadeModel<TModel>
        {
            var keyed = registration.StateName.Length != 0;
            var stateName = registration.StateName;
            if (!keyed)
            {
                services.AddScoped(provider => CreateScopedHolder(registration, provider));
            }
            else
            {
                services.AddKeyedScoped<ConfiglueScopedRuntime<TModel>>(
                    stateName,
                    (provider, _) => CreateScopedHolder(registration, provider)
                );
            }

            IWritableState<TModel> GetHolderRuntime(IServiceProvider provider) =>
                keyed
                    ? provider
                        .GetRequiredKeyedService<ConfiglueScopedRuntime<TModel>>(stateName)
                        .Runtime
                    : provider.GetRequiredService<ConfiglueScopedRuntime<TModel>>().Runtime;

            if (!registration.IsPerSubject)
            {
                AddScoped<IReadOnlyState<TModel>>(
                    keyed,
                    stateName,
                    (provider, _) => GetHolderRuntime(provider)
                );
                AddScoped<IWritableState<TModel>>(
                    keyed,
                    stateName,
                    (provider, _) => GetHolderRuntime(provider)
                );
                AddScoped<ISubjectState<TModel>>(
                    keyed,
                    stateName,
                    (provider, _) => (ISubjectState<TModel>)GetHolderRuntime(provider)
                );
                AddScoped<IConfiglueEditSessions<TModel>>(
                    keyed,
                    stateName,
                    (provider, _) => (IConfiglueEditSessions<TModel>)GetHolderRuntime(provider)
                );
                AddScoped<IConfiglueDiagnostics<TModel>>(
                    keyed,
                    stateName,
                    (provider, _) => (IConfiglueDiagnostics<TModel>)GetHolderRuntime(provider)
                );
                AddScoped<IConfiglueSources<TModel>>(
                    keyed,
                    stateName,
                    (provider, _) => (IConfiglueSources<TModel>)GetHolderRuntime(provider)
                );
                return;
            }

            var subjectAccessorType = registration.SubjectAccessorType!;
            if (!keyed)
            {
                services.AddScoped(provider => new CurrentSubjectState<TModel>(
                    (ISubjectState<TModel>)GetHolderRuntime(provider),
                    (IConfiglueSubjectAccessor)provider.GetRequiredService(subjectAccessorType)
                ));
                services.AddScoped<IReadOnlyState<TModel>>(provider =>
                    provider.GetRequiredService<CurrentSubjectState<TModel>>()
                );
                services.AddScoped<IWritableState<TModel>>(provider =>
                    provider.GetRequiredService<CurrentSubjectState<TModel>>()
                );
                services.AddScoped<IConfiglueDiagnostics<TModel>>(provider =>
                    provider.GetRequiredService<CurrentSubjectState<TModel>>()
                );
                services.AddScoped<IConfiglueEditSessions<TModel>>(provider =>
                    provider.GetRequiredService<CurrentSubjectState<TModel>>()
                );
            }
            else
            {
                services.AddKeyedScoped<CurrentSubjectState<TModel>>(
                    stateName,
                    (provider, _) =>
                        new CurrentSubjectState<TModel>(
                            (ISubjectState<TModel>)GetHolderRuntime(provider),
                            (IConfiglueSubjectAccessor)
                                provider.GetRequiredService(subjectAccessorType)
                        )
                );
                services.AddKeyedScoped<IReadOnlyState<TModel>>(
                    stateName,
                    (provider, key) =>
                        provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                );
                services.AddKeyedScoped<IWritableState<TModel>>(
                    stateName,
                    (provider, key) =>
                        provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                );
                services.AddKeyedScoped<IConfiglueDiagnostics<TModel>>(
                    stateName,
                    (provider, key) =>
                        provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                );
                services.AddKeyedScoped<IConfiglueEditSessions<TModel>>(
                    stateName,
                    (provider, key) =>
                        provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                );
            }
            AddScoped<ISubjectState<TModel>>(
                keyed,
                stateName,
                (provider, _) => (ISubjectState<TModel>)GetHolderRuntime(provider)
            );
            AddScoped<IConfiglueSources<TModel>>(
                keyed,
                stateName,
                (provider, _) => (IConfiglueSources<TModel>)GetHolderRuntime(provider)
            );
        }

        private static IWritableState<TModel> ResolveSharedRuntime<TModel>(
            IServiceProvider provider,
            bool keyed,
            string stateName
        )
            where TModel : IConfiglueFacadeModel<TModel> =>
            keyed
                ? provider.GetRequiredKeyedService<IWritableState<TModel>>(stateName)
                : provider.GetRequiredService<IWritableState<TModel>>();

        private void AddShared<TService>(
            bool keyed,
            string stateName,
            Func<IServiceProvider, object?, TService> factory
        )
            where TService : class
        {
            if (!keyed)
            {
                services.AddSingleton(provider => factory(provider, null));
            }
            else
            {
                services.AddKeyedSingleton(stateName, (provider, key) => factory(provider, key));
            }
        }

        private void AddScoped<TService>(
            bool keyed,
            string stateName,
            Func<IServiceProvider, object?, TService> factory
        )
            where TService : class
        {
            if (!keyed)
            {
                services.AddScoped(provider => factory(provider, null));
            }
            else
            {
                services.AddKeyedScoped(stateName, (provider, key) => factory(provider, key));
            }
        }

        private ConfiglueScopedRuntime<TModel> CreateScopedHolder<TModel>(
            ConfiglueModelRegistration<TModel> registration,
            IServiceProvider provider
        )
            where TModel : IConfiglueFacadeModel<TModel>
        {
            var resources = new List<object>();
            var resourceSet = new HashSet<object>(ReferenceIdentityComparer.Instance);
            IConfiglueRuntimeState<TModel>? runtime = null;
            try
            {
                runtime =
                    (IConfiglueRuntimeState<TModel>)
                        registration.CreateRuntime(
                            provider,
                            resource =>
                            {
                                ArgumentNullException.ThrowIfNull(resource);
                                if (resource is not IDisposable && resource is not IAsyncDisposable)
                                {
                                    throw new ArgumentException(
                                        "An owned resource must implement IDisposable or IAsyncDisposable.",
                                        nameof(resource)
                                    );
                                }

                                if (resourceSet.Add(resource))
                                {
                                    resources.Add(resource);
                                }
                            },
                            hostPaths
                        );
                return new ConfiglueScopedRuntime<TModel>(runtime, resources);
            }
            catch (Exception creationException)
            {
                List<Exception>? cleanupErrors = null;
                try
                {
                    ConfiglueOwnedResources.Dispose(runtime!);
                }
                catch (Exception cleanupException)
                {
                    (cleanupErrors ??= []).Add(cleanupException);
                }

                for (var index = resources.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        ConfiglueOwnedResources.Dispose(resources[index]);
                    }
                    catch (Exception cleanupException)
                    {
                        (cleanupErrors ??= []).Add(cleanupException);
                    }
                }

                if (cleanupErrors is not null)
                {
                    cleanupErrors.Insert(0, creationException);
                    throw new AggregateException(
                        "Scoped runtime creation and cleanup both failed.",
                        cleanupErrors
                    );
                }

                throw;
            }
        }
    }
}
