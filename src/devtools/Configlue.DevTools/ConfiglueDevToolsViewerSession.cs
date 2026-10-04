using Configlue.CompilerServices;

namespace Configlue.DevTools;

/// <summary>
/// Server-side effective-state viewer session for one live state instance.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. The runtime stays server-side; the browser (BlazorMonaco
/// over the Interactive Server circuit) receives the canonical JSON once plus
/// compact decoration/marker deltas afterwards. No runtime object graph is
/// mirrored to the browser and no new transport protocol is introduced.
/// </para>
/// <para>
/// Live refresh follows the existing watcher path: source watcher -&gt;
/// runtime change -&gt; <see cref="Changed"/> -&gt; component re-read via
/// <see cref="RefreshAsync"/> -&gt; circuit update -&gt; Monaco refresh. Each
/// load/refresh performs a single consistent snapshot read; contribution
/// projections reuse the cached snapshot without hidden backend reads.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
public sealed class ConfiglueDevToolsViewerSession<TModel> : IDisposable
    where TModel : IConfiglueFacadeModel<TModel>
{
    private readonly IWritableState<TModel> _state;
    private readonly ConfiglueDevToolsViewerOptions _options;
    private readonly ConfiglueModelSchema _schema;
    private readonly object _gate = new();
    private IDisposable? _subscription;
    private ConfiglueDetailsSnapshot? _snapshot;
    private ConfiglueViewerDocument? _current;
    private long _version;
    private int _disposed;

    /// <summary>Creates a viewer session over an already-constructed live state.</summary>
    public ConfiglueDevToolsViewerSession(
        IWritableState<TModel> state,
        ConfiglueDevToolsViewerOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _options = options ?? ConfiglueDevToolsViewerOptions.Default;
        _schema = ConfiglueModelDescriptor<TModel>.Current.Schema;
        SchemaSetup = ConfiglueDevToolsViewerProjection.BuildSchemaSetup(_schema, _options);
    }

    /// <summary>
    /// Raised when the runtime reports an effective-value change (watcher path).
    /// Handlers re-read through <see cref="RefreshAsync"/>; no read happens here.
    /// </summary>
    public event Action? Changed;

    /// <summary>The generated model schema backing this session.</summary>
    public ConfiglueModelSchema Schema => _schema;

    /// <summary>
    /// Monaco JSON language-service setup. Stable per model/schema; configure once.
    /// </summary>
    public ConfiglueViewerSchemaSetup SchemaSetup { get; }

    /// <summary>The current viewer document, if loaded.</summary>
    public ConfiglueViewerDocument? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>The current document version (0 before load).</summary>
    public long DocumentVersion
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>
    /// Loads the initial/current JSON when the selected state is loaded.
    /// Subscribes to effective-value change notifications; deterministic disposal
    /// via <see cref="Dispose"/>.
    /// </summary>
    public async ValueTask<ConfiglueViewerDocument> LoadAsync(
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            _schema,
            _options,
            1
        );
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _snapshot = snapshot.Details;
            _current = document;
            _version = 1;
            _subscription ??= _state.OnChange(OnStateChanged);
        }

        return document;
    }

    /// <summary>
    /// Re-reads the live state and returns minimal Monaco updates.
    /// Text edits (not full <c>SetValue</c>) preserve scroll/selection;
    /// decoration-only changes carry no document payload.
    /// </summary>
    public async ValueTask<ConfiglueViewerRefreshResult> RefreshAsync(
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ConfiglueViewerDocument? current;
        lock (_gate)
        {
            current = _current;
        }

        if (current is null)
        {
            var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return new ConfiglueViewerRefreshResult(loaded, [], true, true);
        }

        var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var next = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            _schema,
            _options,
            current.DocumentVersion + 1
        );
        var textEdits = ConfiglueDevToolsViewerProjection.ComputeTextEdits(current.Json, next.Json);
        var textChanged = textEdits.Count != 0;
        var decorationsChanged =
            !textChanged && !ConfiglueDevToolsViewerProjection.AreOverlaysEqual(current, next);
        if (!textChanged && !decorationsChanged)
        {
            lock (_gate)
            {
                _snapshot = snapshot.Details;
            }

            return new ConfiglueViewerRefreshResult(current, [], false, false);
        }

        lock (_gate)
        {
            _snapshot = snapshot.Details;
            _current = next;
            _version = next.DocumentVersion;
        }

        return new ConfiglueViewerRefreshResult(next, textEdits, textChanged, decorationsChanged);
    }

    /// <summary>
    /// Normalized per-member contribution projection from the cached snapshot.
    /// Compact metadata only; performs no backend reads.
    /// </summary>
    public string GetContributionJson(string memberPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberPath);
        ConfiglueDetailsSnapshot? snapshot;
        lock (_gate)
        {
            snapshot = _snapshot;
        }

        if (snapshot is null)
        {
            throw new InvalidOperationException(
                "The viewer session has no snapshot yet. Call LoadAsync first."
            );
        }

        return ConfiglueDevToolsViewerProjection.BuildContributionJson(
            snapshot,
            _schema,
            memberPath
        );
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IDisposable? subscription;
        lock (_gate)
        {
            subscription = _subscription;
            _subscription = null;
        }

        subscription?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnStateChanged(TModel value)
    {
        _ = value;
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Changed?.Invoke();
    }

    private async ValueTask<StateSnapshot<TModel>> ReadSnapshotAsync(
        CancellationToken cancellationToken
    )
    {
        if (_state is IConfiglueStateSnapshotRuntime<TModel> runtime)
        {
            return await runtime.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        var value = await _state.GetValueAsync(cancellationToken).ConfigureAwait(false);
        return new StateSnapshot<TModel>(value, details: null);
    }
}
