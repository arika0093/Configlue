using System.Reactive.Linq;

namespace Configlue.Extensions.Reactive;

/// <summary>Adapts Configlue notifications to native Reactive observables for composition.</summary>
public static class ConfiglueReactiveExtensions
{
    /// <summary>Observes future resolved changes without an initial read.</summary>
    public static global::System.IObservable<T> ObserveChanges<T>(this IReadOnlyOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create<T>(options.OnChange);
    }

    /// <summary>Observes the current value followed by resolved changes.</summary>
    /// <remarks>Subscribes before reading and suppresses a stale initial value if a change arrives during the read.</remarks>
    public static global::System.IObservable<T> ObserveValues<T>(this IReadOnlyOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create<T>(options.OnChange, options.GetValueAsync);
    }

    /// <summary>Observes watcher reload failures as exception values without terminating the stream.</summary>
    public static global::System.IObservable<Exception> ObserveReloadFailures<T>(
        this IConfiglueDiagnostics<T> diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return Create<Exception>(diagnostics.OnReloadFailed);
    }

    /// <summary>Observes the current active value, its changes, and subsequent profile switches.</summary>
    public static global::System.IObservable<T> ObserveActiveValues<T>(
        this IConfiglueProfiledOptions<T> profiles
    )
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles
            .ObserveActiveProfileNames()
            .Select(name =>
                global::System
                    .Reactive.Linq.Observable.FromAsync(token =>
                        profiles.GetProfileAsync(name, token).AsTask()
                    )
                    .SelectMany(profile => profile.ObserveValues())
            )
            .Switch();
    }

    /// <summary>Observes the current active profile name followed by profile switches.</summary>
    public static global::System.IObservable<string> ObserveActiveProfileNames<T>(
        this IConfiglueProfiledOptions<T> profiles
    )
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return Create<string>(
            listener =>
            {
                profiles.ActiveProfileChanged += listener;
                return global::System.Reactive.Disposables.Disposable.Create(() =>
                    profiles.ActiveProfileChanged -= listener
                );
            },
            profiles.GetActiveProfileNameAsync
        );
    }

    private static global::System.IObservable<T> Create<T>(
        Func<Action<T>, IDisposable> subscribe,
        Func<CancellationToken, ValueTask<T>>? readInitial = null
    ) =>
        global::System.Reactive.Linq.Observable.Create<T>(observer =>
            CallbackSubscription<T>.Start(subscribe, readInitial, observer.OnNext, observer.OnError)
        );
}
