using System.Text.Json;
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
/// </remarks>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "SonarAnalyzer.CSharp",
    "S2743",
    Justification = "The options instance is stateless configuration shared across all closed model types by design."
)]
public sealed class ConfiglueDevToolsEditorSession<TModel> : IDisposable
    where TModel : IConfiglueFacadeModel<TModel>
{
    private static readonly JsonSerializerOptions StrictModelOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IWritableState<TModel> _state;
    private readonly EditSession<TModel> _session;
    private readonly ConfiglueModelSchema _schema;
    private readonly ConfiglueDevToolsViewerOptions _options;
    private readonly string _stateName;
    private readonly object _gate = new();
    private readonly Action _upstreamHandler;
    private readonly HashSet<string> _secretOverrides = new(StringComparer.Ordinal);
    private ConfiglueViewerDocument _current;
    private List<ConfiglueEditorChangedPath> _modifiedPaths = [];
    private long _documentVersion;
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
        _documentVersion = current.DocumentVersion;
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
        catch (InvalidOperationException exception) when (IsNoWriter(exception))
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
    public static ConfiglueEditorFailureCategory Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            StateMultiWriteException => ConfiglueEditorFailureCategory.PartialWrite,
            Configlue.State.StateConflictException => ConfiglueEditorFailureCategory.Conflict,
            ConfiglueValidationException => ConfiglueEditorFailureCategory.Validation,
            ObjectDisposedException => ConfiglueEditorFailureCategory.Invalidated,
            InvalidOperationException invalid
                when invalid.Message.Contains(
                    "saving, rebasing, or disposed",
                    StringComparison.Ordinal
                ) => ConfiglueEditorFailureCategory.Invalidated,
            InvalidOperationException => ConfiglueEditorFailureCategory.Routing,
            System.Text.Json.JsonException => ConfiglueEditorFailureCategory.Parse,
            _ => ConfiglueEditorFailureCategory.Routing,
        };
    }

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

        JsonNode? draftNode;
        try
        {
            draftNode = JsonNode.Parse(draftJson);
        }
        catch (JsonException exception)
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Parse,
                [$"The draft is not well-formed JSON: {TrimMessage(exception.Message)}"],
                HasUpstreamChanges
            );
        }

        if (draftNode is not JsonObject draftObject)
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Schema,
                ["The draft root must be a JSON object matching the effective state."],
                HasUpstreamChanges
            );
        }

        TModel before;
        lock (_gate)
        {
            before = _session.Value;
        }

        var baselineJson = BuildRedactedJson(before, details: null);
        var baselineNode = JsonNode.Parse(baselineJson) as JsonObject;
        var normalizedDraft =
            ConfiglueEditorSemanticJson.NormalizeKeys(draftObject, _schema, _options.NamingPolicy)
            as JsonObject;
        var normalizedBaseline =
            ConfiglueEditorSemanticJson.NormalizeKeys(baselineNode, _schema, _options.NamingPolicy)
            as JsonObject;
        if (normalizedDraft is null || normalizedBaseline is null)
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Schema,
                ["The draft root must be a JSON object matching the effective state."],
                HasUpstreamChanges
            );
        }

        if (ConfiglueEditorSemanticJson.SemanticEquals(normalizedBaseline, normalizedDraft))
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

        var plaintextSecrets = new List<string>();
        var deletedPlaceholders = new List<string>();
        ConfiglueEditorSemanticJson.ValidateDraftSecrets(
            _schema,
            normalizedDraft,
            normalizedBaseline,
            prefix: string.Empty,
            ancestorSecret: false,
            plaintextSecrets,
            deletedPlaceholders
        );
        if (plaintextSecrets.Count > 0)
        {
            plaintextSecrets.Sort(StringComparer.Ordinal);
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Secret,
                plaintextSecrets
                    .Select(static path =>
                        $"Secret '{path}' must stay '{ConfiglueSecrets.RedactedText}' in the editor; use Change secret instead."
                    )
                    .ToArray(),
                HasUpstreamChanges
            );
        }

        if (deletedPlaceholders.Count > 0)
        {
            deletedPlaceholders.Sort(StringComparer.Ordinal);
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.NonEditable,
                deletedPlaceholders
                    .Select(static path =>
                        $"Secret placeholder '{path}' is a protected read-only range; restore the '{ConfiglueSecrets.RedactedText}' line or discard the draft."
                    )
                    .ToArray(),
                HasUpstreamChanges
            );
        }

        var rawPaths = new List<string>();
        ConfiglueEditorSemanticJson.CollectChangedPaths(
            normalizedBaseline,
            normalizedDraft,
            _schema,
            prefix: string.Empty,
            rawPaths
        );

        var details = await ReadDetailsAsync(cancellationToken).ConfigureAwait(false);
        if (details is not null)
        {
            var blocked = FindBlockedPaths(rawPaths, details);
            blocked.Sort(
                static (left, right) =>
                    string.Compare(left.Path, right.Path, StringComparison.Ordinal)
            );
            if (blocked.Count > 0)
            {
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
        }

        string mergedJson;
        try
        {
            var currentNode = JsonSerializer.SerializeToNode(before) as JsonObject;
            var merged = ConfiglueEditorSemanticJson.MergeDraft(
                currentNode,
                draftObject,
                _schema,
                _options.NamingPolicy
            );
            mergedJson = merged.ToJsonString();
        }
        catch (Exception exception)
            when (exception is InvalidOperationException || exception is JsonException)
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Schema,
                [
                    $"The draft could not be merged over the current value: {TrimMessage(exception.Message)}",
                ],
                HasUpstreamChanges
            );
        }

        TModel desired;
        try
        {
            desired =
                JsonSerializer.Deserialize<TModel>(mergedJson, StrictModelOptions)
                ?? throw new InvalidOperationException(
                    "The DevTools draft did not contain a model value."
                );
        }
        catch (JsonException exception)
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Schema,
                [$"The draft does not match the model shape: {TrimMessage(exception.Message)}"],
                HasUpstreamChanges
            );
        }
        catch (InvalidOperationException exception)
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Schema,
                [$"The draft does not match the model shape: {TrimMessage(exception.Message)}"],
                HasUpstreamChanges
            );
        }

        var secretDiffers = ConfiglueEditorSemanticJson.SecretSubtreeDiffers(
            _schema,
            before,
            desired,
            string.Empty,
            false,
            out var secretDiff
        );
        if (secretDiffers && !IsSecretOverride(secretDiff))
        {
            return ConfiglueEditorSyncResult.Fail(
                ConfiglueEditorFailureCategory.Secret,
                [
                    $"Secret '{secretDiff}' changed outside the explicit Change-secret flow; use Change secret instead.",
                ],
                HasUpstreamChanges
            );
        }

        var changed = DescribeChanges(rawPaths, details);
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
        catch (Exception exception) when (IsMappable(exception))
        {
            return ConfiglueEditorPreviewResult.Fail(
                Classify(exception),
                [DescribeFailure(exception)]
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
            var empty = new ConfiglueEditorWriteReceipt([], 0, true, _stateName, Revision: null);
            return ConfiglueEditorSaveResult.Ok(empty, [], HasUpstreamChanges);
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
                new ConfiglueEditorWriteReceipt(
                    receipt
                        .Sources.Select(static source => new ConfiglueEditorSourceWrite(
                            source.SourceId.ToString(),
                            source.ResourceId?.ToString(),
                            source.Revision
                        ))
                        .ToArray(),
                    receipt.PhysicalWriteCount,
                    receipt.PhysicalWriteCount <= 1,
                    receipt.StateName,
                    receipt.Revision
                ),
                saved.Select(static path => path.MemberPath).ToArray(),
                HasUpstreamChanges
            );
        }
        catch (Exception exception) when (IsMappable(exception))
        {
            if (exception is StateMultiWriteException partial)
            {
                return ConfiglueEditorSaveResult.Fail(
                    ConfiglueEditorFailureCategory.PartialWrite,
                    [DescribePartialWrite(partial)],
                    HasUpstreamChanges
                );
            }

            return ConfiglueEditorSaveResult.Fail(
                Classify(exception),
                [DescribeFailure(exception)],
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
        catch (Exception exception) when (IsMappable(exception))
        {
            return ConfiglueEditorSyncResult.Fail(
                Classify(exception),
                [DescribeFailure(exception)],
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

        ConfiglueMemberPath path;
        try
        {
            path = ConfiglueMemberPath.FromNames(_schema, memberPath);
        }
        catch (ArgumentException exception)
        {
            return new ValueTask<ConfiglueEditorSyncResult>(
                ConfiglueEditorSyncResult.Fail(
                    ConfiglueEditorFailureCategory.Schema,
                    [$"Unknown member path '{memberPath}': {TrimMessage(exception.Message)}"],
                    HasUpstreamChanges
                )
            );
        }

        if (!path.IsSecret())
        {
            return new ValueTask<ConfiglueEditorSyncResult>(
                ConfiglueEditorSyncResult.Fail(
                    ConfiglueEditorFailureCategory.Schema,
                    [
                        $"Member '{memberPath}' is not a secret; edit it in the Monaco draft instead.",
                    ],
                    HasUpstreamChanges
                )
            );
        }

        if (string.Equals(secretText, ConfiglueSecrets.RedactedText, StringComparison.Ordinal))
        {
            return new ValueTask<ConfiglueEditorSyncResult>(
                ConfiglueEditorSyncResult.Fail(
                    ConfiglueEditorFailureCategory.Secret,
                    ["The redacted placeholder is never a valid secret value."],
                    HasUpstreamChanges
                )
            );
        }

        TModel before;
        lock (_gate)
        {
            before = _session.Value;
        }

        TModel desired;
        try
        {
            desired = SetSecretValue(before, memberPath, secretText);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException
                || exception is JsonException
                || exception is ArgumentException
            )
        {
            var category =
                exception is ArgumentException
                    ? ConfiglueEditorFailureCategory.Schema
                    : ConfiglueEditorFailureCategory.Secret;
            return new ValueTask<ConfiglueEditorSyncResult>(
                ConfiglueEditorSyncResult.Fail(
                    category,
                    [TrimMessage(exception.Message)],
                    HasUpstreamChanges
                )
            );
        }

        var introducesSecretDiff = ConfiglueEditorSemanticJson.SecretSubtreeDiffers(
            _schema,
            before,
            desired,
            string.Empty,
            false,
            out _
        );
        if (introducesSecretDiff)
        {
            // The only sanctioned secret difference: record the override so the
            // next draft sync recognizes it as explicit rather than smuggled.
        }

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
            var editability = ResolveEditability(details, memberPath);
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

        return BuildRedactedJson(value, details: null);
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

    private string BuildRedactedJson(TModel value, ConfiglueDetailsSnapshot? details)
    {
        var version = Interlocked.Read(ref _documentVersion);
        return ConfiglueDevToolsViewerProjection
            .BuildDocument(value, details, _schema, _options, version)
            .Json;
    }

    private async ValueTask<ConfiglueDetailsSnapshot?> ReadDetailsAsync(
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

    private List<(string Path, string Editability)> FindBlockedPaths(
        IReadOnlyList<string> changedPaths,
        ConfiglueDetailsSnapshot details
    )
    {
        var blocked = new List<(string Path, string Editability)>();
        foreach (var changed in changedPaths)
        {
            foreach (var candidate in Prefixes(changed))
            {
                var truncated = TruncateAtCollection(candidate);
                ConfiglueMemberPath path;
                try
                {
                    path = ConfiglueMemberPath.FromNames(_schema, truncated);
                }
                catch (ArgumentException)
                {
                    // Unknown members are reported by strict model binding instead.
                    continue;
                }

                ConfiglueEditability editability;
                try
                {
                    editability = details.Editability(path);
                }
                catch (Exception)
                {
                    continue;
                }

                if (editability != ConfiglueEditability.Editable)
                {
                    blocked.Add((candidate, editability.ToString()));
                    break;
                }
            }
        }

        return blocked;
    }

    private List<ConfiglueEditorChangedPath> DescribeChanges(
        IReadOnlyList<string> changedPaths,
        ConfiglueDetailsSnapshot? details
    )
    {
        var described = new List<ConfiglueEditorChangedPath>(changedPaths.Count);
        foreach (
            var changed in changedPaths
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static path => path, StringComparer.Ordinal)
        )
        {
            described.Add(
                new ConfiglueEditorChangedPath(changed, ResolveEditability(details, changed))
            );
        }

        return described;
    }

    private string ResolveEditability(ConfiglueDetailsSnapshot? details, string memberPath)
    {
        if (details is null)
        {
            return ConfiglueEditability.Editable.ToString();
        }

        try
        {
            var truncated = TruncateAtCollection(memberPath);
            return details
                .Editability(ConfiglueMemberPath.FromNames(_schema, truncated))
                .ToString();
        }
        catch (Exception)
        {
            return ConfiglueEditability.Editable.ToString();
        }
    }

    private static IEnumerable<string> Prefixes(string dottedPath)
    {
        var parts = dottedPath.Split('.');
        for (var length = 1; length <= parts.Length; length++)
        {
            yield return string.Join(".", parts.Take(length));
        }
    }

    private string TruncateAtCollection(string dottedPath)
    {
        // Collection elements (Tags[0]) and their descendants resolve editability
        // at the owning collection member; index segments never reach FromNames.
        var cleaned = StripIndices(dottedPath);
        var parts = cleaned.Split('.');
        var current = _schema;
        var kept = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            ConfiglueMemberSchema? found = null;
            foreach (var member in current.Members)
            {
                if (!member.IsDefault && string.Equals(member.Name, part, StringComparison.Ordinal))
                {
                    found = member;
                    break;
                }
            }

            if (found is null)
            {
                break;
            }

            kept.Add(part);
            if (IsCollection(found.Value))
            {
                break;
            }

            try
            {
                current = found.Value.NestedSchemaFactory?.Invoke() ?? current;
            }
            catch (Exception)
            {
                break;
            }

            if (found.Value.NestedSchemaFactory is null)
            {
                // Leaf reached; remaining parts (if any) belong to strict binding.
                break;
            }
        }

        return kept.Count == 0 ? StripIndices(dottedPath) : string.Join(".", kept);
    }

    private static string StripIndices(string path)
    {
        var builder = new System.Text.StringBuilder(path.Length);
        var depth = 0;
        foreach (var ch in path)
        {
            if (ch == '[')
            {
                depth++;
                continue;
            }

            if (ch == ']')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Trim('.');
    }

    private static bool IsCollection(ConfiglueMemberSchema member)
    {
        try
        {
            var type = Nullable.GetUnderlyingType(member.ValueType) ?? member.ValueType;
            return type != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private TModel SetSecretValue(TModel current, string memberPath, string plaintext)
    {
        var node =
            JsonSerializer.SerializeToNode(current) as JsonObject
            ?? throw new InvalidOperationException(
                $"Secret '{memberPath}' cannot be addressed on a non-object model value."
            );
        var parts = memberPath.Split('.');
        var schema = _schema;
        var target = node;
        for (var index = 0; index < parts.Length; index++)
        {
            ConfiglueMemberSchema? found = null;
            foreach (var member in schema.Members)
            {
                if (
                    !member.IsDefault
                    && string.Equals(member.Name, parts[index], StringComparison.Ordinal)
                )
                {
                    found = member;
                    break;
                }
            }

            if (found is null)
            {
                throw new ArgumentException(
                    $"Property path '{memberPath}' contains unknown member '{parts[index]}'.",
                    nameof(memberPath)
                );
            }

            var key = ResolveNodeKey(target, found.Value, _options.NamingPolicy);
            if (index == parts.Length - 1)
            {
                if (IsCollection(found.Value))
                {
                    throw new InvalidOperationException(
                        $"Secret '{memberPath}' is a collection; replace it through the Monaco draft's normal merge semantics instead."
                    );
                }

                target[key] = plaintext;
                break;
            }

            if (target[key] is not JsonObject child)
            {
                throw new InvalidOperationException(
                    $"Secret '{memberPath}' traverses non-object member '{found.Value.Name}'."
                );
            }

            target = child;
            schema =
                found.Value.NestedSchemaFactory?.Invoke()
                ?? throw new InvalidOperationException(
                    $"Secret '{memberPath}' continues through non-nested member '{found.Value.Name}'."
                );
        }

        return JsonSerializer.Deserialize<TModel>(node.ToJsonString(), StrictModelOptions)
            ?? throw new InvalidOperationException(
                "The secret change did not produce a model value."
            );
    }

    private static string ResolveNodeKey(
        JsonObject node,
        ConfiglueMemberSchema member,
        JsonNamingPolicy? namingPolicy
    )
    {
        if (node.ContainsKey(member.Name))
        {
            return member.Name;
        }

        string? converted = null;
        try
        {
            converted = namingPolicy?.ConvertName(member.Name);
        }
        catch (Exception)
        {
            converted = null;
        }

        if (converted is not null && node.ContainsKey(converted))
        {
            return converted;
        }

        foreach (var (candidate, _) in node)
        {
            if (string.Equals(candidate, member.Name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            if (
                converted is not null
                && string.Equals(candidate, converted, StringComparison.OrdinalIgnoreCase)
            )
            {
                return candidate;
            }
        }

        return member.Name;
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

        try
        {
            var snapshot = await DevToolsSnapshotReader
                .ReadAsync(_state, cancellationToken)
                .ConfigureAwait(false);
            var version = Interlocked.Increment(ref _documentVersion);
            var document = ConfiglueDevToolsViewerProjection.BuildDocument(
                snapshot.Value,
                snapshot.Details,
                _schema,
                _options,
                version
            );
            lock (_gate)
            {
                _current = document;
            }

            Changed?.Invoke();
        }
        catch (Exception)
        {
            // Baseline refresh is best-effort; the committed draft stays valid and
            // the next refresh retries.
        }
    }

    private static bool IsMappable(Exception exception) =>
        exception
            is StateMultiWriteException
                or Configlue.State.StateConflictException
                or ConfiglueValidationException
                or ObjectDisposedException
                or InvalidOperationException;

    private static bool IsNoWriter(InvalidOperationException exception) =>
        exception.Message.Contains("No writable", StringComparison.Ordinal);

    private static string DescribeFailure(Exception exception)
    {
        var message = TrimMessage(exception.Message);
        if (exception is ConfiglueValidationException validation)
        {
            return $"Validation failed: {string.Join("; ", validation.Failures)}";
        }

        if (exception is StateMultiWriteException partial)
        {
            return DescribePartialWrite(partial);
        }

        return message;
    }

    private static string DescribePartialWrite(StateMultiWriteException exception)
    {
        string Sources(IReadOnlyList<SourceId> sources) =>
            sources.Count == 0
                ? "—"
                : string.Join(", ", sources.Select(static source => source.ToString()));
        return $"A multi-source write failed after partial completion. Completed {exception.Completed.Sources.Count} source(s); failed: {Sources(exception.FailedSourceIds)}; unattempted: {Sources(exception.UnattemptedSourceIds)}. Cause: {TrimMessage(exception.InnerException?.Message ?? exception.Message)}";
    }

    private static string TrimMessage(string message)
    {
        message = message.Trim();
        const int maxLength = 500;
        return message.Length <= maxLength ? message : message.Substring(0, maxLength) + "…";
    }
}
