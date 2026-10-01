using Configlue;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Hosting.AspNetCore;

/// <summary>Resolves a Configlue subject from the current ASP.NET Core request.</summary>
public sealed class HttpContextConfiglueSubjectAccessor<TSubject>
    : IConfiglueSubjectAccessor<TSubject>
    where TSubject : IConfiglueSubject
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly Func<HttpContext, CancellationToken, ValueTask<TSubject>> _resolveSubject;

    /// <summary>Creates an accessor that maps the active request to a subject.</summary>
    public HttpContextConfiglueSubjectAccessor(
        IHttpContextAccessor httpContextAccessor,
        Func<HttpContext, CancellationToken, ValueTask<TSubject>> resolveSubject
    )
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(resolveSubject);
        _httpContextAccessor = httpContextAccessor;
        _resolveSubject = resolveSubject;
    }

    /// <inheritdoc />
    public ValueTask<TSubject> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
        {
            throw new InvalidOperationException(
                "The current Configlue subject requires an active HTTP request."
            );
        }

        return _resolveSubject(context, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<IConfiglueSubject> GetCurrentSubjectAsync(
        CancellationToken cancellationToken = default
    ) => await GetCurrentAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>Registers an HTTP-context-backed accessor as a scoped service.</summary>
public static class HttpContextConfiglueSubjectAccessorServiceCollectionExtensions
{
    /// <summary>Registers a scoped accessor that maps each request to its Configlue subject.</summary>
    public static IServiceCollection AddHttpContextConfiglueSubjectAccessor<TSubject>(
        this IServiceCollection services,
        Func<HttpContext, CancellationToken, ValueTask<TSubject>> resolveSubject
    )
        where TSubject : IConfiglueSubject
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resolveSubject);
        services.AddHttpContextAccessor();
        services.AddScoped<HttpContextConfiglueSubjectAccessor<TSubject>>(
            provider => new HttpContextConfiglueSubjectAccessor<TSubject>(
                provider.GetRequiredService<IHttpContextAccessor>(),
                resolveSubject
            )
        );
        services.AddScoped<IConfiglueSubjectAccessor<TSubject>>(provider =>
            provider.GetRequiredService<HttpContextConfiglueSubjectAccessor<TSubject>>()
        );
        return services;
    }

    /// <summary>Registers a scoped accessor with a synchronous request-to-subject mapper.</summary>
    public static IServiceCollection AddHttpContextConfiglueSubjectAccessor<TSubject>(
        this IServiceCollection services,
        Func<HttpContext, TSubject> resolveSubject
    )
        where TSubject : IConfiglueSubject
    {
        ArgumentNullException.ThrowIfNull(resolveSubject);
        return services.AddHttpContextConfiglueSubjectAccessor<TSubject>(
            (context, _) => ValueTask.FromResult(resolveSubject(context))
        );
    }
}

/// <summary>Host-registration extensions for ASP.NET Core subject resolution.</summary>
public static class HttpContextConfiglueSubjectRegistrationExtensions
{
    /// <summary>Resolves the current subject from the active HTTP request.</summary>
    public static ConfiglueSubjectRegistrationBuilder<TSubject> FromHttpContext<TSubject>(
        this ConfiglueSubjectRegistrationBuilder<TSubject> builder,
        Func<HttpContext, CancellationToken, ValueTask<TSubject>> resolveSubject
    )
        where TSubject : IConfiglueSubject
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpContextConfiglueSubjectAccessor(resolveSubject);
        return builder;
    }

    /// <summary>Resolves the current subject from the active HTTP request.</summary>
    public static ConfiglueSubjectRegistrationBuilder<TSubject> FromHttpContext<TSubject>(
        this ConfiglueSubjectRegistrationBuilder<TSubject> builder,
        Func<HttpContext, TSubject> resolveSubject
    )
        where TSubject : IConfiglueSubject
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resolveSubject);
        builder.Services.AddHttpContextConfiglueSubjectAccessor(resolveSubject);
        return builder;
    }
}
