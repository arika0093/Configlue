using System.Collections.Concurrent;
using System.Security.Claims;
using Configlue;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Resource.Http.AspNetCore;

/// <summary>Resolves a Configlue subject from the current Blazor authentication state.</summary>
public sealed class BlazorAuthenticationConfiglueSubjectAccessor<TSubject>
    : IConfiglueSubjectAccessor<TSubject>,
        IConfiglueSubjectChangeSource,
        IDisposable
    where TSubject : IConfiglueSubject
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly Func<ClaimsPrincipal, CancellationToken, ValueTask<TSubject>> _resolveSubject;
    private readonly ConcurrentDictionary<long, Action> _listeners = new();
    private long _nextListenerId;
    private int _disposed;

    /// <summary>Creates an accessor scoped to one Blazor authentication-state provider.</summary>
    public BlazorAuthenticationConfiglueSubjectAccessor(
        AuthenticationStateProvider authenticationStateProvider,
        Func<ClaimsPrincipal, CancellationToken, ValueTask<TSubject>> resolveSubject
    )
    {
        ArgumentNullException.ThrowIfNull(authenticationStateProvider);
        ArgumentNullException.ThrowIfNull(resolveSubject);
        _authenticationStateProvider = authenticationStateProvider;
        _resolveSubject = resolveSubject;
        _authenticationStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    /// <inheritdoc />
    public async ValueTask<TSubject> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var state = await _authenticationStateProvider
            .GetAuthenticationStateAsync()
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await _resolveSubject(state.User, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IConfiglueSubject> GetCurrentSubjectAsync(
        CancellationToken cancellationToken = default
    ) => await GetCurrentAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public IDisposable OnChange(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var id = Interlocked.Increment(ref _nextListenerId);
        _listeners.TryAdd(id, listener);
        return new ListenerRegistration(this, id);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _authenticationStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        _listeners.Clear();
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> _)
    {
        foreach (var listener in _listeners.Values)
        {
            try
            {
                listener();
            }
            catch (Exception)
            {
                // One scoped consumer must not prevent other subject views from rebinding.
            }
        }
    }

    private void RemoveListener(long id) => _listeners.TryRemove(id, out _);

    private sealed class ListenerRegistration(
        BlazorAuthenticationConfiglueSubjectAccessor<TSubject> owner,
        long id
    ) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.RemoveListener(id);
            }
        }
    }
}

/// <summary>Registers a scoped subject accessor backed by Blazor authentication state.</summary>
public static class BlazorAuthenticationConfiglueSubjectAccessorServiceCollectionExtensions
{
    /// <summary>Registers a scoped accessor that maps each authentication principal.</summary>
    public static IServiceCollection AddBlazorAuthenticationConfiglueSubjectAccessor<TSubject>(
        this IServiceCollection services,
        Func<ClaimsPrincipal, CancellationToken, ValueTask<TSubject>> resolveSubject
    )
        where TSubject : IConfiglueSubject
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resolveSubject);
        services.AddScoped<BlazorAuthenticationConfiglueSubjectAccessor<TSubject>>(
            provider => new BlazorAuthenticationConfiglueSubjectAccessor<TSubject>(
                provider.GetRequiredService<AuthenticationStateProvider>(),
                resolveSubject
            )
        );
        services.AddScoped<IConfiglueSubjectAccessor<TSubject>>(provider =>
            provider.GetRequiredService<BlazorAuthenticationConfiglueSubjectAccessor<TSubject>>()
        );
        return services;
    }
}
