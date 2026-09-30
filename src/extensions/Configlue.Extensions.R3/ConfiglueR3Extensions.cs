using R3;

namespace Configlue.Extensions.R3;

/// <summary>Adapts Configlue notifications to native R3 observables for composition.</summary>
public static class ConfiglueR3Extensions
{
    /// <summary>Observes future resolved changes without an initial read.</summary>
    public static global::R3.Observable<T> ObserveChanges<T>(this IReadOnlyState<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create<T>(options.OnChange);
    }

    /// <summary>Observes the current value followed by resolved changes.</summary>
    /// <remarks>Subscribes before reading and suppresses a stale initial value if a change arrives during the read.</remarks>
    public static global::R3.Observable<T> ObserveValues<T>(this IReadOnlyState<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create<T>(options.OnChange, options.GetValueAsync);
    }

    /// <summary>Observes watcher reload failures as exception values without terminating the stream.</summary>
    public static global::R3.Observable<Exception> ObserveReloadFailures<T>(
        this IConfiglueDiagnostics<T> diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return Create<Exception>(diagnostics.OnReloadFailed);
    }

    /// <summary>Observes the current active value, its changes, and subsequent profile switches.</summary>
    public static global::R3.Observable<T> ObserveActiveValues<T>(
        this IConfiglueProfiledState<T> profiles
    )
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles
            .ObserveActiveProfileNames()
            .Select(name =>
                global::R3
                    .Observable.FromAsync(token => profiles.GetProfileAsync(name, token))
                    .SelectMany(profile => profile.ObserveValues())
            )
            .Switch();
    }

    /// <summary>Observes the current active profile name followed by profile switches.</summary>
    public static global::R3.Observable<string> ObserveActiveProfileNames<T>(
        this IConfiglueProfiledState<T> profiles
    )
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return Create<string>(
            listener =>
            {
                profiles.ActiveProfileChanged += listener;
                return global::R3.Disposable.Create(() =>
                    profiles.ActiveProfileChanged -= listener
                );
            },
            profiles.GetActiveProfileNameAsync
        );
    }

    private static global::R3.Observable<T> Create<T>(
        Func<Action<T>, IDisposable> subscribe,
        Func<CancellationToken, ValueTask<T>>? readInitial = null
    ) =>
        global::R3.Observable.Create<T>(observer =>
            CallbackSubscription<T>.Start(
                subscribe,
                readInitial,
                observer.OnNext,
                exception => observer.OnCompleted(global::R3.Result.Failure(exception))
            )
        );
}
