using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Owns application listener registration and failure-isolated dispatch for one
/// runtime: change, reload, and reload-failure subscribers.
///
/// Listener lists live here, not alongside source waiting or subject-table
/// concurrency. Mutation runs under the <see cref="RuntimeLifetime"/> gate owned
/// by the caller; notification snapshots the lists through the same gate so
/// delivery races with shutdown exactly as before. A throwing listener is
/// isolated and recorded, never breaking delivery to the remaining listeners.
/// </summary>
internal sealed class RuntimeWatchNotificationHub<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeLifetime _lifetime;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeModelCloner<TModel, TFragment> _cloner;
    private readonly List<Action<TModel>> _changeListeners = [];
    private readonly List<Action<Exception>> _reloadFailureListeners = [];
    private readonly List<Action<StateRevisionVector?>> _reloadListeners = [];

    internal RuntimeWatchNotificationHub(
        RuntimeLifetime lifetime,
        RuntimeDiagnosticRecorder diagnostics,
        RuntimeModelCloner<TModel, TFragment> cloner
    )
    {
        _lifetime = lifetime;
        _diagnostics = diagnostics;
        _cloner = cloner;
    }

    /// <summary>Adds a change listener. Call under the lifetime gate.</summary>
    internal IDisposable AddChangeListenerCore(Action<TModel> listener)
    {
        _changeListeners.Add(listener);
        return new ChangeSubscription(this, listener);
    }

    /// <summary>Adds a reload listener. Call under the lifetime gate.</summary>
    internal IDisposable AddReloadListenerCore(Action<StateRevisionVector?> listener)
    {
        _reloadListeners.Add(listener);
        return new ReloadSubscription(this, listener);
    }

    /// <summary>Adds a reload-failure listener. Call under the lifetime gate.</summary>
    internal IDisposable AddReloadFailureListenerCore(Action<Exception> listener)
    {
        _reloadFailureListeners.Add(listener);
        return new ReloadFailureSubscription(this, listener);
    }

    /// <summary>Removes a change listener. Runs under the gate; allowed after disposal.</summary>
    internal void RemoveChangeListener(Action<TModel> listener) =>
        _lifetime.Unregister(() =>
        {
            _changeListeners.Remove(listener);
        });

    /// <summary>Removes a reload listener. Runs under the gate; allowed after disposal.</summary>
    internal void RemoveReloadListener(Action<StateRevisionVector?> listener) =>
        _lifetime.Unregister(() =>
        {
            _reloadListeners.Remove(listener);
        });

    /// <summary>
    /// Removes a reload-failure listener. Runs under the gate; allowed after disposal.
    /// </summary>
    internal void RemoveReloadFailureListener(Action<Exception> listener) =>
        _lifetime.Unregister(() =>
        {
            _reloadFailureListeners.Remove(listener);
        });

    /// <summary>Clears all listener lists. Call under the lifetime gate.</summary>
    internal void ClearCore()
    {
        _changeListeners.Clear();
        _reloadFailureListeners.Clear();
        _reloadListeners.Clear();
    }

    /// <summary>Delivers a changed value to a snapshot of the change listeners.</summary>
    internal void NotifyChanged(TModel value)
    {
        if (!_lifetime.TrySnapshot(_changeListeners, out var listeners))
        {
            return;
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(_cloner.Clone(value));
            }
            catch (Exception exception)
            {
                _diagnostics.Record(
                    ConfiglueDiagnosticEventKind.ObserverFailed,
                    errorCategory: exception.GetType().FullName
                );
            }
        }
    }

    /// <summary>Delivers a revision-only reload to a snapshot of the reload listeners.</summary>
    internal void NotifyReloaded(StateRevisionVector? revisions)
    {
        if (!_lifetime.TrySnapshot(_reloadListeners, out var listeners))
        {
            return;
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(revisions);
            }
            catch (Exception listenerException)
            {
                _diagnostics.Record(
                    ConfiglueDiagnosticEventKind.ObserverFailed,
                    errorCategory: listenerException.GetType().FullName
                );
            }
        }
    }

    /// <summary>Delivers a reload failure to a snapshot of the failure listeners.</summary>
    internal void NotifyReloadFailed(Exception exception)
    {
        if (!_lifetime.TrySnapshot(_reloadFailureListeners, out var listeners))
        {
            return;
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(exception);
            }
            catch (Exception listenerException)
            {
                _diagnostics.Record(
                    ConfiglueDiagnosticEventKind.ObserverFailed,
                    errorCategory: listenerException.GetType().FullName
                );
            }
        }
    }

    private sealed class ChangeSubscription(
        RuntimeWatchNotificationHub<TModel, TFragment> owner,
        Action<TModel> listener
    ) : IDisposable
    {
        private RuntimeWatchNotificationHub<TModel, TFragment>? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveChangeListener(listener);
    }

    private sealed class ReloadFailureSubscription(
        RuntimeWatchNotificationHub<TModel, TFragment> owner,
        Action<Exception> listener
    ) : IDisposable
    {
        private RuntimeWatchNotificationHub<TModel, TFragment>? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveReloadFailureListener(listener);
    }

    private sealed class ReloadSubscription(
        RuntimeWatchNotificationHub<TModel, TFragment> owner,
        Action<StateRevisionVector?> listener
    ) : IDisposable
    {
        private RuntimeWatchNotificationHub<TModel, TFragment>? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveReloadListener(listener);
    }
}
