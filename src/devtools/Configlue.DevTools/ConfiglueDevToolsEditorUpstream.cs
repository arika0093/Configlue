namespace Configlue.DevTools;

using Configlue.CompilerServices;

/// <summary>
/// Upstream synchronization and baseline state behind the DevTools editor session.
/// </summary>
/// <remarks>
/// <para>
/// Internal to the DevTools package. Owns the canonical baseline document and its
/// version: the diff original and the redacted JSON the Monaco model reloads
/// from after every commit, rebase, or discard. Core rebase/conflict semantics
/// stay in <see cref="EditSession{T}"/>; this collaborator only refreshes the
/// projection baseline around it. Baseline refresh is best-effort: the committed
/// draft stays valid and the next refresh retries.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
internal sealed class ConfiglueDevToolsEditorUpstream<TModel>
    where TModel : IConfiglueFacadeModel<TModel>
{
    private readonly IWritableState<TModel> _state;
    private readonly EditSession<TModel> _session;
    private readonly ConfiglueModelSchema _schema;
    private readonly ConfiglueDevToolsViewerOptions _options;
    private long _documentVersion;

    public ConfiglueDevToolsEditorUpstream(
        IWritableState<TModel> state,
        EditSession<TModel> session,
        ConfiglueModelSchema schema,
        ConfiglueDevToolsViewerOptions options,
        long initialVersion
    )
    {
        _state = state;
        _session = session;
        _schema = schema;
        _options = options;
        _documentVersion = initialVersion;
    }

    /// <summary>Builds the canonical redacted JSON for a draft value.</summary>
    public string BuildRedactedJson(TModel value)
    {
        var version = Interlocked.Read(ref _documentVersion);
        return ConfiglueDevToolsViewerProjection
            .BuildDocument(value, snapshot: null, _schema, _options, version)
            .Json;
    }

    /// <summary>
    /// Reads the current details snapshot without blocking editing on failure.
    /// </summary>
    public async ValueTask<ConfiglueDetailsSnapshot?> ReadDetailsAsync(
        CancellationToken cancellationToken
    )
    {
        if (_state is IConfiglueDetailsRuntime detailsRuntime)
        {
            try
            {
                return await detailsRuntime
                    .GetDetailsSnapshotAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A failing details read must not block editing; fall back to the
                // session-start transport and let commit enforce routing.
            }
        }

        try
        {
            return _session.SessionStart.Details;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Rebuilds the baseline document from the latest snapshot.
    /// Returns null when the baseline cannot be refreshed.
    /// </summary>
    public async ValueTask<ConfiglueViewerDocument?> RebuildBaselineAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            var snapshot = await DevToolsSnapshotReader
                .ReadAsync(_state, cancellationToken)
                .ConfigureAwait(false);
            var version = Interlocked.Increment(ref _documentVersion);
            return ConfiglueDevToolsViewerProjection.BuildDocument(
                snapshot.Value,
                snapshot.Details,
                _schema,
                _options,
                version
            );
        }
        catch (Exception)
        {
            return null;
        }
    }
}
