using Configlue;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Resource.Http.AspNetCore;

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
