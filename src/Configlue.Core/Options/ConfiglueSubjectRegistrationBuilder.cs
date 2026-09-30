using Microsoft.Extensions.DependencyInjection;

namespace Configlue;

/// <summary>Configures the host integration used to resolve the current per-subject state.</summary>
/// <typeparam name="TSubject">The application subject resolved for the current host context.</typeparam>
/// <remarks>
/// The builder is returned by <see cref="ConfiglueSubjectServiceCollectionExtensions.AddConfiglueSubject{TSubject}"/>.
/// Host-specific packages provide extensions such as <c>FromHttpContext</c> and
/// <c>FromBlazorAuthenticationState</c> that register the matching scoped
/// <see cref="IConfiglueSubjectAccessor{TSubject}"/>.
/// </remarks>
public sealed class ConfiglueSubjectRegistrationBuilder<TSubject>
    where TSubject : IConfiglueSubject
{
    internal ConfiglueSubjectRegistrationBuilder(IServiceCollection services) =>
        Services = services;

    /// <summary>The subject type being configured.</summary>
    public Type SubjectType => typeof(TSubject);

    /// <summary>The service collection receiving the host-specific accessor registration.</summary>
    public IServiceCollection Services { get; }
}

/// <summary>Registers per-subject host integration for a Configlue subject.</summary>
public static class ConfiglueSubjectServiceCollectionExtensions
{
    /// <summary>Starts registering the host integration used to resolve a per-subject state.</summary>
    public static ConfiglueSubjectRegistrationBuilder<TSubject> AddConfiglueSubject<TSubject>(
        this IServiceCollection services
    )
        where TSubject : IConfiglueSubject
    {
        ArgumentNullException.ThrowIfNull(services);
        return new ConfiglueSubjectRegistrationBuilder<TSubject>(services);
    }
}
