using Configlue.CompilerServices;

namespace Configlue.DevTools;

/// <summary>
/// Explicit opt-in collection of already-constructed live state instances for DevTools.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Bind instances that the application already created; this registry
/// never rediscovers or re-executes application bootstrap code.
/// </para>
/// <para>
/// Non-DI usage:
/// <code>
/// var devtools = new ConfiglueDevToolsRegistry();
/// devtools.Add(context.GetState&lt;AppSettings&gt;());
/// </code>
/// </para>
/// </remarks>
public sealed class ConfiglueDevToolsRegistry
{
    private readonly object _gate = new();
    private readonly List<IConfiglueDevToolsEntry> _entries = [];
    private readonly List<Func<IReadOnlyList<IConfiglueDevToolsEntry>>> _dynamic = [];

    /// <summary>The bound state infos in registration order, including live dynamic names.</summary>
    public IReadOnlyList<ConfiglueDevToolsStateInfo> States
    {
        get
        {
            var result = new List<ConfiglueDevToolsStateInfo>();
            lock (_gate)
            {
                foreach (var entry in _entries)
                {
                    result.Add(entry.Info);
                }

                foreach (var factory in _dynamic)
                {
                    foreach (var entry in factory())
                    {
                        result.Add(entry.Info);
                    }
                }
            }

            return result.AsReadOnly();
        }
    }

    internal int BoundCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count + _dynamic.Count;
            }
        }
    }

    /// <summary>
    /// Binds an already-constructed live state instance.
    /// </summary>
    /// <typeparam name="TModel">The generated configuration model.</typeparam>
    /// <param name="state">The live runtime instance.</param>
    /// <param name="stateName">The state-name identity for <c>(TModel, StateName)</c>.</param>
    public ConfiglueDevToolsRegistry Add<TModel>(
        IWritableState<TModel> state,
        string? stateName = null
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(state);
        var name = stateName ?? string.Empty;
        lock (_gate)
        {
            if (
                _entries
                    .OfType<ConfiglueDevToolsEntry<TModel>>()
                    .Any(candidate =>
                        string.Equals(candidate.Info.StateName, name, StringComparison.Ordinal)
                        && string.Equals(
                            candidate.Info.ModelId,
                            ConfiglueModelDescriptor<TModel>.Current.Schema.Id,
                            StringComparison.Ordinal
                        )
                    )
            )
            {
                throw new ArgumentException(
                    $"A DevTools state for model '{typeof(TModel)}' with state name '{name}' is already bound.",
                    nameof(stateName)
                );
            }

            _entries.Add(new ConfiglueDevToolsEntry<TModel>(state, name));
        }

        return this;
    }

    /// <summary>
    /// Binds a live dynamic named-state registry. Names are resolved against the live
    /// registry at request time.
    /// </summary>
    public ConfiglueDevToolsRegistry AddRegistry<TModel>(IConfiglueStateRegistry<TModel> registry)
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(registry);
        lock (_gate)
        {
            _dynamic.Add(() =>
                registry
                    .StateNames.OrderBy(static name => name, StringComparer.Ordinal)
                    .Select(name =>
                        (IConfiglueDevToolsEntry)
                            new ConfiglueDevToolsRegistryEntry<TModel>(registry, name)
                    )
                    .ToArray()
            );
        }

        return this;
    }

    /// <summary>Resolves one bound entry by model id and state name.</summary>
    internal bool TryGet(string modelId, string stateName, out IConfiglueDevToolsEntry? entry)
    {
        lock (_gate)
        {
            entry = _entries
                .Concat(_dynamic.SelectMany(static factory => factory()))
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Info.ModelId, modelId, StringComparison.Ordinal)
                    && string.Equals(candidate.Info.StateName, stateName, StringComparison.Ordinal)
                );
            return entry is not null;
        }
    }

    internal IReadOnlyList<IConfiglueDevToolsEntry> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<IConfiglueDevToolsEntry>(_entries);
            foreach (var factory in _dynamic)
            {
                result.AddRange(factory());
            }

            return result.AsReadOnly();
        }
    }
}
