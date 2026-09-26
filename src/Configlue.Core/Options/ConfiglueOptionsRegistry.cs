using System.Diagnostics;

namespace Configlue;

/// <summary>A thread-safe registry that creates and owns named Configlue options profiles.</summary>
/// <typeparam name="TModel">The configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
public sealed class ConfiglueOptionsRegistry<TModel, TFragment> : IConfiglueOptionsRegistry<TModel>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly Func<string, ConfiglueOptions<TModel, TFragment>> _factory;
    private readonly object _gate = new();
    private readonly Dictionary<string, ConfiglueOptions<TModel, TFragment>> _profiles = new(
        StringComparer.Ordinal
    );
    private bool _disposed;

    /// <summary>Creates a registry using a factory that builds a profile from its name.</summary>
    public ConfiglueOptionsRegistry(Func<string, ConfiglueOptions<TModel, TFragment>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc />
    public event Action<string, IWritableOptions<TModel>>? ProfileAdded;

    /// <inheritdoc />
    public event Action<string>? ProfileRemoved;

    /// <inheritdoc />
    public IReadOnlyCollection<string> ProfileNames
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_profiles.Keys.ToArray());
            }
        }
    }

    /// <inheritdoc />
    public IWritableOptions<TModel> Get(string profileName)
    {
        ValidateName(profileName);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _profiles.TryGetValue(profileName, out var options)
                ? options
                : throw new KeyNotFoundException(
                    $"Configlue profile '{profileName}' is not registered."
                );
        }
    }

    /// <inheritdoc />
    public bool TryGet(string profileName, out IWritableOptions<TModel>? options)
    {
        ValidateName(profileName);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_profiles.TryGetValue(profileName, out var registered))
            {
                options = registered;
                return true;
            }

            options = null;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryAdd(string profileName)
    {
        ValidateName(profileName);
        ConfiglueOptions<TModel, TFragment> options;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_profiles.ContainsKey(profileName))
            {
                return false;
            }

            options =
                _factory(profileName)
                ?? throw new InvalidOperationException("The profile factory returned null.");
            _profiles.Add(profileName, options);
        }

        NotifyAdded(profileName, options);
        return true;
    }

    /// <inheritdoc />
    public bool TryRemove(string profileName)
    {
        ValidateName(profileName);
        ConfiglueOptions<TModel, TFragment>? options;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_profiles.Remove(profileName, out options))
            {
                return false;
            }
        }

        options.Dispose();
        NotifyRemoved(profileName);
        return true;
    }

    /// <inheritdoc />
    public void Clear()
    {
        KeyValuePair<string, ConfiglueOptions<TModel, TFragment>>[] removed;
        lock (_gate)
        {
            ThrowIfDisposed();
            removed = _profiles.ToArray();
            _profiles.Clear();
        }

        foreach (var (name, options) in removed)
        {
            options.Dispose();
            NotifyRemoved(name);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        KeyValuePair<string, ConfiglueOptions<TModel, TFragment>>[] removed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            removed = _profiles.ToArray();
            _profiles.Clear();
        }

        foreach (var (name, options) in removed)
        {
            options.Dispose();
            NotifyRemoved(name);
        }
    }

    private void NotifyAdded(string name, IWritableOptions<TModel> options)
    {
        var handlers = ProfileAdded;
        if (handlers is null)
        {
            return;
        }

        foreach (
            var handler in handlers
                .GetInvocationList()
                .Cast<Action<string, IWritableOptions<TModel>>>()
        )
        {
            try
            {
                handler(name, options);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Configlue profile-added listener failed: {0}", exception);
            }
        }
    }

    private void NotifyRemoved(string name)
    {
        var handlers = ProfileRemoved;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(name);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Configlue profile-removed listener failed: {0}", exception);
            }
        }
    }

    private static void ValidateName(string profileName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
