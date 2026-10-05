using System.Text.Json.Nodes;
using Configlue.CompilerServices;

namespace Configlue.DevTools;

/// <summary>
/// Server-side semantic editing session behind the BlazorMonaco DevTools editor.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. The Monaco JSON document is an effective-state projection,
/// never a storage document: saving text never writes a file. Browser drafts flow
/// through debounced/explicit synchronization into this session as
/// <c>parse -&gt; semantic diff -&gt; EditSession draft -&gt; Configlue validation
/// -&gt; write routing -&gt; source persistence</c>. No DevTools-specific write
/// pipeline is introduced; a normal <see cref="EditSession{T}"/> is owned per
/// selected state and Monaco holds the session draft.
/// </para>
/// <para>
/// Typing stays browser-local (gated by <see cref="ConfiglueDevToolsDraftThrottle"/>);
/// browser Monaco JSON-schema feedback stays advisory while server-side Configlue
/// validation is authoritative on preview/commit. Read-only members are rejected
/// with an explanation, never bypassed. Secrets never enter the Monaco model:
/// placeholders stay placeholders and real changes arrive only through
/// <see cref="ApplySecretAsync"/>.
/// </para>
/// <para>
/// Value mapping is explicit, never guessed: absent members reset to the model
/// default, explicit JSON null maps to null, present members map to their value.
/// </para>
/// <para>
/// The session owns user-visible state and lifecycle and delegates each
/// implementation concern to a focused internal collaborator: the draft pipeline
/// (parse/normalize/diff/merge), the editability guard (schema/editability
/// validation), the secret flow (explicit secret mutations), the upstream
/// tracker (baseline/rebase state), and the failure classifier (commit/discard
/// failure mapping). Core rebase/conflict semantics stay in
/// <see cref="EditSession{T}"/> and are reused, never duplicated.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
public sealed class ConfiglueDevToolsEditorSession<TModel> : IDisposable
    where TModel : IConfiglueFacadeModel<TModel>
{
    private readonly IWritableState<TModel> _state;
    private readonly EditSession<TModel> _session;
    private readonly ConfiglueModelSchema _schema;
    private readonly ConfiglueDevToolsViewerOptions _options;
    private readonly string _stateName;
    private readonly object _gate = new();
    private readonly Action _upstreamHandler;
    private readonly HashSet<string> _secretOverrides = new(StringComparer.Ordinal);
    private readonly ConfiglueDevToolsEditorDraftPipeline<TModel> _drafts;
    private readonly ConfiglueDevToolsEditorEditabilityGuard _guards;
    private readonly ConfiglueDevToolsEditorSecretFlow<TModel> _secrets;
    private readonly ConfiglueDevToolsEditorUpstream<TModel> _upstream;
    private ConfiglueViewerDocument _current;
    private List<ConfiglueEditorChangedPath> _modifiedPaths = [];
    private int _disposed;

    private ConfiglueDevToolsEditorSession(
        IWritableState<TModel> state,
        EditSession<TModel> session,
        ConfiglueModelSchema schema,
        ConfiglueDevToolsViewerOptions options,
        string stateName,
        ConfiglueViewerDocument current
    )
    {
        _state = state;
        _session = session;
        _schema = schema;
        _options = options;
        _stateName = stateName;
        _current = current;
        _drafts = new ConfiglueDevToolsEditorDraftPipeline<TModel>(schema, options.NamingPolicy);
        _guards = new ConfiglueDevToolsEditorEditabilityGuard(schema);
        _secrets = new ConfiglueDevToolsEditorSecretFlow<TModel>(schema, options.NamingPolicy);
        _upstream = new ConfiglueDevToolsEditorUpstream<TModel>(
            state,
            session,
            schema,
            options,
            current.DocumentVersion
        );
        _upstreamHandler = OnUpstreamChanged;
        _session.UpstreamChanged += _upstreamHandler;
    }

    /// <summary>
    /// Raised when the runtime reports an effective-value change (watcher path) or
    /// after commit/discard/rebase replaced the draft baseline.
    /// </summary>
    public event Action? Changed;

    /// <summary>The generated model schema backing this session.</summary>
    public ConfiglueModelSchema Schema => _schema;

    /// <summary>The state-name identity this session is bound to.</summary>
    public string StateName => _stateName;

    /// <summary>
    /// Human-readable subject/state identity (<c>modelId:stateName</c>). A session is
    /// pinned to one selected state; selecting another state requires opening a new
    /// session so a stale draft can never be committed elsewhere.
    /// </summary>
    public string StateIdentity =>
        string.IsNullOrEmpty(_stateName) ? _schema.Id : $"{_schema.Id}:{_stateName}";

    /// <summary>
    /// The session-start viewer document: the diff original and the canonical
    /// redacted JSON the Monaco model was loaded from.
    /// </summary>
    public ConfiglueViewerDocument SessionStartDocument => _current;

    /// <summary>
    /// The current baseline viewer document (session-start, rebuilt after every
    /// commit/rebase/discard). Monaco reloads from <see cref="BuildCanonicalDraftJson"/>
    /// when this moves.
    /// </summary>
    public ConfiglueViewerDocument CurrentDocument
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Whether the owned edit session currently holds local changes.</summary>
    public bool HasLocalChanges
    {
        get
        {
            lock (_gate)
            {
                return Volatile.Read(ref _disposed) == 0 && _session.HasLocalChanges;
            }
        }
    }

    /// <summary>Whether upstream changed since the current baseline.</summary>
    public bool HasUpstreamChanges
    {
        get
        {
            lock (_gate)
            {
                return Volatile.Read(ref _disposed) == 0 && _session.HasUpstreamChanges;
            }
        }
    }

    /// <summary>Member paths changed by the last synchronized draft.</summary>
    public IReadOnlyList<ConfiglueEditorChangedPath> ModifiedPaths
    {
        get
        {
            lock (_gate)
            {
                return _modifiedPaths.AsReadOnly();
            }
        }
    }

    /// <summary>Whether this session has been disposed (commits then invalidate).</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Opens a semantic editing session over an already-constructed live state.
    /// </summary>
    /// <param name="state">The live writable state to edit.</param>
    /// <param name="options">Viewer projection options (naming policy, indent).</param>
    /// <param name="stateName">The state-name identity for <c>(TModel, StateName)</c>.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <exception cref="InvalidOperationException">
    /// The state does not support edit sessions or has no writable source.
    /// </exception>
    public static async ValueTask<ConfiglueDevToolsEditorSession<TModel>> OpenAsync(
        IWritableState<TModel> state,
        ConfiglueDevToolsViewerOptions? options = null,
        string stateName = "",
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state is not IConfiglueEditSessions<TModel> sessions)
        {
            throw new InvalidOperationException(
                $"State '{typeof(TModel).FullName}' does not support edit sessions."
            );
        }

        var resolvedOptions = options ?? ConfiglueDevToolsViewerOptions.Default;
        EditSession<TModel> session;
        try
        {
            session = await sessions.OpenEditSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
            when (ConfiglueDevToolsEditorFailures.IsNoWriter(exception))
        {
            throw new InvalidOperationException(
                $"State '{typeof(TModel).FullName}' has no writable source; DevTools editing is unavailable.",
                exception
            );
        }

        try
        {
            var schema = ConfiglueModelDescriptor<TModel>.Current.Schema;
            var start = session.SessionStart;
            var document = ConfiglueDevToolsViewerProjection.BuildDocument(
                start.Value,
                start.Details,
                schema,
                resolvedOptions,
                1
            );
            return new ConfiglueDevToolsEditorSession<TModel>(
                state,
                session,
                schema,
                resolvedOptions,
                stateName,
                document
            );
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Monaco JSON language-service setup. Stable per model/schema; configure once.
    /// </summary>
    public ConfiglueViewerSchemaSetup GetSchemaSetup() =>
        ConfiglueDevToolsViewerProjection.BuildSchemaSetup(_schema, _options);

    /// <summary>
    /// Classifies an exception without hiding its semantic category.
    /// </summary>
    public static ConfiglueEditorFailureCategory Classify(Exception exception) =>
        ConfiglueDevToolsEditorFailures.Classify(exception);

    /// <summary>
    /// Synchronizes one Monaco draft into the owned edit session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parse errors, schema violations, non-editable changes, and secret-plaintext
    /// smuggling are rejected with their category and never touch the draft.
    /// Formatting-only, ordering-only, and value-equivalent changes produce no
    /// patch. This performs no writes.
    /// </para>
    /// </remarks>
    /// <param name="draftJson">The full Monaco draft text (post-debounce or explicit).</param>
    /// <param name="cancellationToken">Cancels the synchronization.</param>
    public async ValueTask<ConfiglueEditorSyncResult> SyncDraftAsync(
        string draftJson,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(draftJson);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Invalidated,
                ["The editing session was closed; reopen it before syncing."],
                hasUpstreamChanges: false
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (
            !_drafts.TryParseRoot(
                draftJson,
                out var draftObject,
                out var parseCategory,
                out var parseError
            ) || draftObject is null
        )
        {
            return ConfiglueEditorSyncResult.Fail(parseCategory, [parseError], HasUpstreamChanges);
        }

        TModel before;
        lock (_gate)
        {
            before = _session.Value;
        }

        var baselineJson = _upstream.BuildRedactedJson(before);
        if (
            !_drafts.TryNormalize(
                draftObject,
                baselineJson,
                out var normalizedDraft,
                out var normalizedBaseline
            )
            || normalizedDraft is null
            || normalizedBaseline is null
        )
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Schema,
                ["The draft root must be a JSON object matching the effective state."],
                HasUpstreamChanges
            );
        }

        if (
            ConfiglueDevToolsEditorDraftPipeline<TModel>.IsNoOp(normalizedBaseline, normalizedDraft)
        )
        {
            return DescribeStaged();
        }

        var secretGuard = GuardDraftSecrets(normalizedDraft, normalizedBaseline);
        if (secretGuard is not null)
        {
            return secretGuard;
        }

        var rawPaths = _drafts.CollectChanges(normalizedBaseline, normalizedDraft);

        var details = await _upstream.ReadDetailsAsync(cancellationToken).ConfigureAwait(false);
        var editabilityGuard = GuardEditability(rawPaths, details);
        if (editabilityGuard is not null)
        {
            return editabilityGuard;
        }

        if (!_drafts.TryMerge(before, draftObject, out var desired, out var mergeError))
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Schema,
                [mergeError],
                HasUpstreamChanges
            );
        }

        if (_secrets.IsSmuggled(before, desired, IsSecretOverride, out var secretDiff))
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Secret,
                [
                    $"Secret '{secretDiff}' changed outside the explicit Change-secret flow; use Change secret instead.",
                ],
                HasUpstreamChanges
            );
        }

        return PublishDraft(desired, rawPaths, details);
    }

    /// <summary>
    /// Dry-runs routing plus Configlue validation for the current draft without
    /// mutating any source or resource.
    /// </summary>
    public async ValueTask<ConfiglueEditorPreviewResult> ValidateAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ConfiglueEditorPreviewResult.Fail(
                ConfiglueEditorFailureCategory.Invalidated,
                ["The editing session was closed; reopen it before validating."]
            );
        }

        TModel desired;
        bool hasChanges;
        lock (_gate)
        {
            desired = _session.Value;
            hasChanges = _session.HasLocalChanges;
        }

        if (!hasChanges)
        {
            return ConfiglueEditorPreviewResult.Ok(
                physicalWriteCount: 0,
                isAtomic: true,
                isEmpty: true
            );
        }

        if (_state is not IConfiglueWritePreview<TModel> preview)
        {
            return ConfiglueEditorPreviewResult.Unavailable();
        }

        try
        {
            var planned = await preview
                .PreviewWriteAsync(desired, cancellationToken)
                .ConfigureAwait(false);
            return ConfiglueEditorPreviewResult.Ok(
                planned.PhysicalWriteCount,
                planned.IsAtomic,
                planned.IsEmpty
            );
        }
        catch (Exception exception) when (ConfiglueDevToolsEditorFailures.IsMappable(exception))
        {
            return ConfiglueEditorPreviewResult.Fail(
                ConfiglueDevToolsEditorFailures.Classify(exception),
                [ConfiglueDevToolsEditorFailures.DescribeFailure(exception)]
            );
        }
    }

    /// <summary>
    /// Commits the draft through normal Configlue validation and write routing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One logical save may route members to different sources; every touched
    /// source is reported, never flattened. A commit with no local changes
    /// succeeds without writing.
    /// </para>
    /// </remarks>
    public async ValueTask<ConfiglueEditorSaveResult> CommitAsync(
        CancellationToken cancellationToken = default
    )
    {
        bool hasChanges;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return ConfiglueEditorSaveResult.Fail(
                    ConfiglueEditorFailureCategory.Invalidated,
                    ["The editing session was closed; reopen it before saving."],
                    hasUpstreamChanges: false
                );
            }

            hasChanges = _session.HasLocalChanges;
        }

        if (!hasChanges)
        {
            return ConfiglueEditorSaveResult.Ok(
                ConfiglueDevToolsEditorFailures.EmptyReceipt(_stateName),
                [],
                HasUpstreamChanges
            );
        }

        try
        {
            var receipt = await _session.CommitAsync(cancellationToken).ConfigureAwait(false);
            List<ConfiglueEditorChangedPath> saved;
            lock (_gate)
            {
                saved = _modifiedPaths;
                _modifiedPaths = [];
                _secretOverrides.Clear();
            }

            await RebuildCurrentAsync(cancellationToken).ConfigureAwait(false);
            return ConfiglueEditorSaveResult.Ok(
                ConfiglueDevToolsEditorFailures.ToWriteReceipt(receipt, _stateName),
                saved.Select(static path => path.MemberPath).ToArray(),
                HasUpstreamChanges
            );
        }
        catch (Exception exception) when (ConfiglueDevToolsEditorFailures.IsMappable(exception))
        {
            if (exception is StateMultiWriteException partial)
            {
                return ConfiglueEditorSaveResult.Fail(
                    ConfiglueEditorFailureCategory.PartialWrite,
                    [ConfiglueDevToolsEditorFailures.DescribePartialWrite(partial)],
                    HasUpstreamChanges
                );
            }

            return ConfiglueEditorSaveResult.Fail(
                ConfiglueDevToolsEditorFailures.Classify(exception),
                [ConfiglueDevToolsEditorFailures.DescribeFailure(exception)],
                HasUpstreamChanges
            );
        }
    }

    /// <summary>Discards local draft changes in favor of the session-start value.</summary>
    public void DiscardChanges()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _session.ResetToSessionStart();
            _modifiedPaths = [];
            _secretOverrides.Clear();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Rebases local draft changes onto the latest upstream snapshot.
    /// </summary>
    /// <remarks>
    /// Conflicts surface as a <see cref="ConfiglueEditorFailureCategory.Conflict"/>
    /// result; the draft is preserved so the user can choose rebase or discard.
    /// Core rebase semantics stay in <see cref="EditSession{T}"/>.
    /// </remarks>
    public async ValueTask<ConfiglueEditorSyncResult> RebaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Invalidated,
                ["The editing session was closed; reopen it before rebasing."],
                hasUpstreamChanges: false
            );
        }

        try
        {
            await _session.RebaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ConfiglueDevToolsEditorFailures.IsMappable(exception))
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueDevToolsEditorFailures.Classify(exception),
                [ConfiglueDevToolsEditorFailures.DescribeFailure(exception)],
                HasUpstreamChanges
            );
        }

        lock (_gate)
        {
            _modifiedPaths = [];
            _secretOverrides.Clear();
        }

        await RebuildCurrentAsync(cancellationToken).ConfigureAwait(false);
        return ConfiglueEditorSyncResult.Ok([], HasLocalChanges, HasUpstreamChanges);
    }

    /// <summary>
    /// Follows upstream when the session is clean. Dirty sessions are never
    /// silently overwritten; they report <c>false</c> so the UI can offer
    /// rebase/discard choices.
    /// </summary>
    /// <returns>True when a clean session rebased onto upstream.</returns>
    public async ValueTask<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        bool clean;
        lock (_gate)
        {
            clean = !_session.HasLocalChanges;
        }

        if (!clean || !HasUpstreamChanges)
        {
            return false;
        }

        var rebased = await RebaseAsync(cancellationToken).ConfigureAwait(false);
        return rebased.Success;
    }

    /// <summary>
    /// Applies an explicit secret change outside the Monaco text model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty plaintext means unchanged. The fake placeholder is always refused.
    /// Plaintext lives only in this call and the owned draft; callers clear their
    /// input immediately after commit/cancel and it never enters Monaco undo
    /// history, storage, hovers, or the initial render.
    /// </para>
    /// </remarks>
    /// <param name="memberPath">Generated member path of a secret member.</param>
    /// <param name="plaintext">The new secret value, or empty for unchanged.</param>
    /// <param name="cancellationToken">Cancels the secret change.</param>
    public ValueTask<ConfiglueEditorSyncResult> ApplySecretAsync(
        string memberPath,
        string? plaintext,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberPath);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new ValueTask<ConfiglueEditorSyncResult>(
                ConfiglueEditorSyncResult.Fail(
                    ConfiglueEditorFailureCategory.Invalidated,
                    ["The editing session was closed; reopen it before changing secrets."],
                    hasUpstreamChanges: false
                )
            );
        }

        var secretText = plaintext ?? string.Empty;
        if (secretText.Length == 0)
        {
            return new ValueTask<ConfiglueEditorSyncResult>(
                ConfiglueEditorSyncResult.Ok(ModifiedPaths, HasLocalChanges, HasUpstreamChanges)
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!_secrets.TryResolvePath(memberPath, out _, out var pathError))
        {
            return new ValueTask<ConfiglueEditorSyncResult>(
                ConfiglueEditorSyncResult.Fail(
                    ConfiglueEditorFailureCategory.Schema,
                    [pathError],
                    HasUpstreamChanges
                )
            );
        }

        TModel before;
        lock (_gate)
        {
            before = _session.Value;
        }

        if (
            !_secrets.TrySetValue(
                before,
                memberPath,
                secretText,
                out var desired,
                out var setCategory,
                out var setError
            )
        )
        {
            return new ValueTask<ConfiglueEditorSyncResult>(
                ConfiglueEditorSyncResult.Fail(setCategory, [setError], HasUpstreamChanges)
            );
        }

        // The only sanctioned secret difference: the override below lets the
        // next draft sync recognize it as explicit rather than smuggled.
        List<ConfiglueEditorChangedPath> changed;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new ValueTask<ConfiglueEditorSyncResult>(
                    ConfiglueEditorSyncResult.Fail(
                        ConfiglueEditorFailureCategory.Invalidated,
                        ["The editing session was closed while changing the secret."],
                        hasUpstreamChanges: false
                    )
                );
            }

            _session.Value = desired;
            _secretOverrides.Add(memberPath);
            var details = _session.SessionStart.Details;
            var editability = _guards.ResolveEditability(details, memberPath);
            changed = _modifiedPaths
                .Where(existing =>
                    !string.Equals(existing.MemberPath, memberPath, StringComparison.Ordinal)
                )
                .Concat([new ConfiglueEditorChangedPath(memberPath, editability)])
                .ToList();
            _modifiedPaths = changed;
            return new ValueTask<ConfiglueEditorSyncResult>(
                ConfiglueEditorSyncResult.Ok(
                    changed.AsReadOnly(),
                    _session.HasLocalChanges,
                    _session.HasUpstreamChanges
                )
            );
        }
    }

    /// <summary>
    /// Builds the canonical redacted JSON for the current draft (for Monaco reload
    /// after save/rebase/discard and for the diff modified pane).
    /// </summary>
    public string BuildCanonicalDraftJson()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        TModel value;
        lock (_gate)
        {
            value = _session.Value;
        }

        return _upstream.BuildRedactedJson(value);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.UpstreamChanged -= _upstreamHandler;
        _session.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnUpstreamChanged()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Changed?.Invoke();
    }

    private ConfiglueEditorSyncResult DescribeStaged()
    {
        // Formatting, ordering, or spelling-only differences: no patch.
        // Previously staged changes (if any) are preserved and reported.
        List<ConfiglueEditorChangedPath> staged;
        bool dirty;
        bool upstream;
        lock (_gate)
        {
            staged = _modifiedPaths;
            dirty = Volatile.Read(ref _disposed) == 0 && _session.HasLocalChanges;
            upstream = Volatile.Read(ref _disposed) == 0 && _session.HasUpstreamChanges;
        }

        return ConfiglueEditorSyncResult.Ok(staged.AsReadOnly(), dirty, upstream);
    }

    private ConfiglueEditorSyncResult? GuardDraftSecrets(
        JsonObject normalizedDraft,
        JsonObject normalizedBaseline
    )
    {
        _secrets.CheckDraft(
            normalizedDraft,
            normalizedBaseline,
            out var plaintextSecrets,
            out var deletedPlaceholders
        );
        if (plaintextSecrets.Count > 0)
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Secret,
                ConfiglueDevToolsEditorSecretFlow<TModel>.DraftPlaintextErrors(plaintextSecrets),
                HasUpstreamChanges
            );
        }

        if (deletedPlaceholders.Count > 0)
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.NonEditable,
                ConfiglueDevToolsEditorSecretFlow<TModel>.DeletedPlaceholderErrors(
                    deletedPlaceholders
                ),
                HasUpstreamChanges
            );
        }

        return null;
    }

    private ConfiglueEditorSyncResult? GuardEditability(
        IReadOnlyList<string> rawPaths,
        ConfiglueDetailsSnapshot? details
    )
    {
        if (details is null)
        {
            return null;
        }

        var blocked = _guards.FindBlockedPaths(rawPaths, details);
        blocked.Sort(
            static (left, right) => string.Compare(left.Path, right.Path, StringComparison.Ordinal)
        );
        if (blocked.Count == 0)
        {
            return null;
        }

        return ConfiglueEditorSyncResult.Fail(
            ConfiglueEditorFailureCategory.NonEditable,
            blocked
                .Select(static item =>
                    $"Member '{item.Path}' is not editable ({item.Editability}); the normal save path cannot change its effective value."
                )
                .ToArray(),
            HasUpstreamChanges
        );
    }

    private ConfiglueEditorSyncResult PublishDraft(
        TModel desired,
        IReadOnlyList<string> rawPaths,
        ConfiglueDetailsSnapshot? details
    )
    {
        var changed = _guards.DescribeChanges(rawPaths, details);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return ConfiglueEditorSyncResult.Fail(
                    ConfiglueEditorFailureCategory.Invalidated,
                    ["The editing session was closed while syncing."],
                    hasUpstreamChanges: false
                );
            }

            _session.Value = desired;
            _modifiedPaths = changed;
            return ConfiglueEditorSyncResult.Ok(
                _modifiedPaths.AsReadOnly(),
                _session.HasLocalChanges,
                _session.HasUpstreamChanges
            );
        }
    }

    private bool IsSecretOverride(string diffPath)
    {
        lock (_gate)
        {
            return _secretOverrides.Contains(diffPath);
        }
    }

    private async ValueTask RebuildCurrentAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var document = await _upstream
            .RebuildBaselineAsync(cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
        {
            return;
        }

        lock (_gate)
        {
            _current = document;
        }

        Changed?.Invoke();
    }
}
