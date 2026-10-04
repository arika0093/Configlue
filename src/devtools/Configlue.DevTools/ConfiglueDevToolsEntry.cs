using System.Text.Json;
using Configlue.CompilerServices;

namespace Configlue.DevTools;

/// <summary>
/// Host-neutral projection over one already-constructed live state instance.
/// </summary>
/// <remarks>
/// Internal to the DevTools package. Consumes only existing stable surfaces:
/// effective-value reads, the details-snapshot transport behind generated
/// <c>GetDetailsAsync()</c>, diagnostics snapshots/events, <c>Check()</c>,
/// edit sessions, schema metadata, and registries. No new inspection API.
/// </remarks>
internal interface IConfiglueDevToolsEntry
{
    ConfiglueDevToolsStateInfo Info { get; }

    /// <summary>
    /// The generated model type backing this entry. Internal-only dispatch metadata
    /// for the DevTools web UI; not a new public inspection API.
    /// </summary>
    Type ModelType { get; }

    /// <summary>
    /// The live writable state as an untyped reference. Resolved against the live
    /// runtime (or live named-state registry) at call time; never rediscovered.
    /// </summary>
    object UntypedState { get; }

    ValueTask<string> GetStateJsonAsync(CancellationToken cancellationToken);

    string GetSchemaJson();

    /// <summary>
    /// Returns cached topology + runtime snapshots without source reads or checks.
    /// Never triggers <c>Check()</c>; safe to call on tab open.
    /// </summary>
    ValueTask<string> GetDiagnosticsJsonAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns value/provenance statistics from one details snapshot.
    /// Performs the same resolution read as <c>GetDetailsAsync()</c> but never
    /// triggers <c>Check()</c>. Counts only; never emits values.
    /// </summary>
    ValueTask<string> GetStatsJsonAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the bounded recent-event timeline from cached history.
    /// No source reads and never triggers <c>Check()</c>.
    /// </summary>
    ValueTask<string> GetEventsJsonAsync(CancellationToken cancellationToken);

    ValueTask<string> RunCheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies a canonical JSON payload through the normal edit-session write routing.
    /// Returns a summary; throws when nothing is bound or the payload is invalid.
    /// </summary>
    ValueTask<string> ApplyJsonAsync(string incomingJson, CancellationToken cancellationToken);

    /// <summary>
    /// Builds the current effective-state viewer document (single consistent read).
    /// </summary>
    ValueTask<ConfiglueViewerDocument> GetViewerDocumentAsync(
        ConfiglueDevToolsViewerOptions? options,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Builds the current viewer document, omitting the JSON text when the caller-known
    /// version already carries identical text (decoration-only delta, no full-doc traffic).
    /// </summary>
    ValueTask<ConfiglueViewerDelta> GetViewerDeltaAsync(
        long knownVersion,
        ConfiglueDevToolsViewerOptions? options,
        CancellationToken cancellationToken
    );

    /// <summary>Gets the Monaco JSON language-service setup (stable per model/schema).</summary>
    ConfiglueViewerSchemaSetup GetViewerSchemaSetup(ConfiglueDevToolsViewerOptions? options);

    /// <summary>
    /// Builds a normalized per-member contribution projection (compact metadata only).
    /// </summary>
    ValueTask<string> GetContributionJsonAsync(
        string memberPath,
        ConfiglueDevToolsViewerOptions? options,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Viewer delta for one refresh. Internal to the DevTools package.
/// </summary>
internal sealed record ConfiglueViewerDelta(ConfiglueViewerDocument Document, bool JsonOmitted);

internal static class ConfiglueDevToolsJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}

internal sealed class ConfiglueDevToolsEntry<TModel> : IConfiglueDevToolsEntry
    where TModel : IConfiglueFacadeModel<TModel>
{
    private readonly IWritableState<TModel> _state;
    private readonly string _stateName;
    private readonly ConfiglueModelSchema _schema;
    private readonly object _viewerGate = new();
    private long _viewerVersion;
    private readonly Dictionary<long, string> _viewerHashes = new();

    public ConfiglueDevToolsEntry(IWritableState<TModel> state, string stateName)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _stateName = stateName ?? string.Empty;
        _schema = ConfiglueModelDescriptor<TModel>.Current.Schema;
    }

    public ConfiglueDevToolsStateInfo Info =>
        new(
            _schema.Id,
            _schema.Version,
            _stateName,
            string.IsNullOrEmpty(_stateName) ? _schema.Id : $"{_schema.Id}:{_stateName}",
            typeof(TModel).FullName ?? typeof(TModel).Name
        );

    /// <inheritdoc />
    public Type ModelType => typeof(TModel);

    /// <inheritdoc />
    public object UntypedState => _state;

    public async ValueTask<string> GetStateJsonAsync(CancellationToken cancellationToken)
    {
        var value = await _state.GetValueAsync(cancellationToken).ConfigureAwait(false);
        return ConfiglueDevToolsProjection.ToRedactedJson(value, _schema);
    }

    public string GetSchemaJson() => ConfiglueDevToolsProjection.ToSchemaJson(_schema);

    public ValueTask<string> GetDiagnosticsJsonAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var diagnostics = (IConfiglueDiagnostics<TModel>)_state;
        var topology = diagnostics.GetDiagnostics();
        var runtime = diagnostics.GetRuntimeSnapshot();
        var recent = diagnostics.GetRecentEvents();
        var payload = new
        {
            state = new
            {
                modelId = runtime.ModelId,
                modelVersion = runtime.ModelVersion,
                stateName = topology.StateName,
                subject = DescribeSubject(runtime.LastResolution?.SubjectKey),
                lastResolution = DescribeEvent(runtime.LastResolution),
                lastReload = DescribeEvent(runtime.LastReload),
                lastWrite = DescribeEvent(runtime.LastWrite),
                lastMigration = DescribeEvent(runtime.LastMigration),
                validation = DescribeValidation(recent, runtime.LastResolution),
            },
            defaultWriteSourceId = topology.DefaultWriteSourceId?.ToString(),
            defaultWriteSourceIsInferred = topology.DefaultWriteSourceIsInferred,
            sources = DescribeSources(topology, runtime),
            cachedNote = "Cached status only; no active Check() was run.",
        };
        return new ValueTask<string>(
            JsonSerializer.Serialize(payload, ConfiglueDevToolsJson.Options)
        );
    }

    public async ValueTask<string> GetStatsJsonAsync(CancellationToken cancellationToken)
    {
        var runtime = (IConfiglueDetailsRuntime)_state;
        var snapshot = await runtime
            .GetDetailsSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        return ConfiglueDevToolsStats.ComputeStatsJson(snapshot);
    }

    public ValueTask<string> GetEventsJsonAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var diagnostics = (IConfiglueDiagnostics<TModel>)_state;
        var recent = diagnostics.GetRecentEvents();
        const int maxEvents = 50;
        var window =
            recent.Count <= maxEvents ? recent : recent.Skip(recent.Count - maxEvents).ToArray();
        var payload = new
        {
            maxBound = maxEvents,
            totalRetained = recent.Count,
            returned = window.Count,
            historyEnabledNote = "Empty when EventHistoryCapacity is zero (default).",
            events = window
                .Select(static item => new
                {
                    sequence = item.Sequence,
                    timestamp = item.Timestamp,
                    kind = item.Kind.ToString(),
                    stateName = item.StateName,
                    subject = DescribeSubject(item.SubjectKey),
                    sourceId = item.SourceId?.ToString(),
                    sourceKind = item.SourceKind,
                    readStatus = item.ReadStatus?.ToString(),
                    hasRevision = item.HasRevision,
                    errorCategory = item.ErrorCategory,
                    canceled = item.Canceled,
                    effectiveValueChanged = item.EffectiveValueChanged,
                })
                .ToArray(),
        };
        return new ValueTask<string>(
            JsonSerializer.Serialize(payload, ConfiglueDevToolsJson.Options)
        );
    }

    public async ValueTask<string> RunCheckAsync(CancellationToken cancellationToken)
    {
        var diagnostics = (IConfiglueDiagnostics<TModel>)_state;
        var operation = diagnostics.Check(cancellationToken);
        var sources = new List<object>();
        await foreach (
            var source in operation.WithCancellation(cancellationToken).ConfigureAwait(false)
        )
        {
            sources.Add(
                new
                {
                    source = source.Source.ToString(),
                    status = source.Status.ToString(),
                    contributed = source.Contributed,
                    fallbackContinued = source.FallbackContinued,
                }
            );
        }

        var result = await operation.Result.ConfigureAwait(false);
        var payload = new
        {
            status = result.Status.ToString(),
            isResolved = result.IsResolved,
            sources,
        };
        return JsonSerializer.Serialize(payload, ConfiglueDevToolsJson.Options);
    }

    public async ValueTask<string> ApplyJsonAsync(
        string incomingJson,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incomingJson);
        var edits = (IConfiglueEditSessions<TModel>)_state;
        var current = await _state.GetValueAsync(cancellationToken).ConfigureAwait(false);
        var currentJson = JsonSerializer.Serialize(current, ConfiglueDevToolsJson.Options);
        var mergedJson = ConfiglueDevToolsProjection.MergePreservingRedactedSecrets(
            string.Empty,
            currentJson,
            incomingJson,
            _schema
        );
        var desired = ConfiglueDevToolsProjection.DeserializeModel<TModel>(mergedJson);
        using var session = await edits
            .OpenEditSessionAsync(cancellationToken)
            .ConfigureAwait(false);
        session.Value = desired;
        var receipt = await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        var payload = new { committed = true, revision = receipt.Revision?.ToString() };
        return JsonSerializer.Serialize(payload, ConfiglueDevToolsJson.Options);
    }

    public async ValueTask<ConfiglueViewerDocument> GetViewerDocumentAsync(
        ConfiglueDevToolsViewerOptions? options,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await DevToolsSnapshotReader
            .ReadAsync(_state, cancellationToken)
            .ConfigureAwait(false);
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            _schema,
            options,
            NextViewerVersion()
        );
        RememberViewer(document);
        return document;
    }

    public async ValueTask<ConfiglueViewerDelta> GetViewerDeltaAsync(
        long knownVersion,
        ConfiglueDevToolsViewerOptions? options,
        CancellationToken cancellationToken
    )
    {
        var document = await GetViewerDocumentAsync(options, cancellationToken)
            .ConfigureAwait(false);
        lock (_viewerGate)
        {
            if (
                _viewerHashes.TryGetValue(knownVersion, out var knownJson)
                && string.Equals(knownJson, document.Json, StringComparison.Ordinal)
            )
            {
                return new ConfiglueViewerDelta(document with { Json = string.Empty }, true);
            }
        }

        return new ConfiglueViewerDelta(document, false);
    }

    public ConfiglueViewerSchemaSetup GetViewerSchemaSetup(
        ConfiglueDevToolsViewerOptions? options
    ) => ConfiglueDevToolsViewerProjection.BuildSchemaSetup(_schema, options);

    public async ValueTask<string> GetContributionJsonAsync(
        string memberPath,
        ConfiglueDevToolsViewerOptions? options,
        CancellationToken cancellationToken
    )
    {
        _ = options;
        ArgumentException.ThrowIfNullOrWhiteSpace(memberPath);
        var snapshot = await DevToolsSnapshotReader
            .ReadAsync(_state, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.Details is null)
        {
            throw new InvalidOperationException(
                "This state implementation does not expose contribution details."
            );
        }

        return ConfiglueDevToolsViewerProjection.BuildContributionJson(
            snapshot.Details,
            _schema,
            memberPath
        );
    }

    private long NextViewerVersion()
    {
        lock (_viewerGate)
        {
            _viewerVersion++;
            return _viewerVersion;
        }
    }

    private void RememberViewer(ConfiglueViewerDocument document)
    {
        lock (_viewerGate)
        {
            _viewerHashes[document.DocumentVersion] = document.Json;
            foreach (
                var oldest in _viewerHashes
                    .Keys.OrderBy(static key => key)
                    .Take(Math.Max(0, _viewerHashes.Count - 8))
                    .ToArray()
            )
            {
                _viewerHashes.Remove(oldest);
            }
        }
    }

    private static string DescribeSubject(SubjectKey? key) =>
        key is null || key.Value.IsDefault ? "default" : key.Value.Value;

    private static object? DescribeEvent(ConfiglueDiagnosticEvent? item) =>
        item is null
            ? null
            : new
            {
                kind = item.Value.Kind.ToString(),
                timestamp = item.Value.Timestamp,
                readStatus = item.Value.ReadStatus?.ToString(),
                hasRevision = item.Value.HasRevision,
                errorCategory = item.Value.ErrorCategory,
                canceled = item.Value.Canceled,
            };

    private static object DescribeValidation(
        IReadOnlyList<ConfiglueDiagnosticEvent> recent,
        ConfiglueDiagnosticEvent? lastResolution
    )
    {
        ConfiglueDiagnosticEvent? lastFailure = null;
        foreach (var item in recent)
        {
            if (item.Kind == ConfiglueDiagnosticEventKind.ValidationFailed)
            {
                lastFailure = item;
            }
        }

        if (lastFailure is not null)
        {
            return new
            {
                status = "Failed",
                lastFailureTimestamp = (DateTimeOffset?)lastFailure.Value.Timestamp,
                lastFailureError = lastFailure.Value.ErrorCategory,
            };
        }

        return new
        {
            status = lastResolution is not null ? "Ok" : "Unknown",
            lastFailureTimestamp = (DateTimeOffset?)null,
            lastFailureError = (string?)null,
        };
    }

    private static object[] DescribeSources(
        ConfiglueStateDiagnostics topology,
        ConfiglueRuntimeDiagnosticSnapshot runtime
    )
    {
        var runtimeById = new Dictionary<string, ConfiglueRuntimeSourceSnapshot>(
            StringComparer.Ordinal
        );
        foreach (var source in runtime.Sources)
        {
            runtimeById[source.Id.ToString()] = source;
        }

        var rows = new List<object>(topology.Sources.Count);
        var order = 0;
        foreach (var source in topology.Sources)
        {
            runtimeById.TryGetValue(source.Id.ToString(), out var observed);
            var hasObserved = runtimeById.ContainsKey(source.Id.ToString());
            rows.Add(
                new
                {
                    id = source.Id.ToString(),
                    order,
                    priority = source.Priority,
                    canRead = source.CanRead,
                    canWrite = source.CanWrite,
                    canWatch = source.CanWatch,
                    isActive = source.IsActive,
                    physicalOrigin = source.PhysicalOrigin,
                    kind = hasObserved ? observed.Kind : (string?)null,
                    isWatching = hasObserved && observed.IsWatching,
                    lastSuccessfulRead = hasObserved
                        ? observed.LastSuccessfulRead
                        : (DateTimeOffset?)null,
                    lastWatchSignal = hasObserved
                        ? observed.LastWatchSignal
                        : (DateTimeOffset?)null,
                    lastRead = hasObserved ? DescribeEvent(observed.LastRead) : null,
                    revisionPresent = hasObserved && (observed.LastRead?.HasRevision == true),
                    lastError = hasObserved ? observed.LastRead?.ErrorCategory : null,
                }
            );
            order++;
        }

        return rows.ToArray();
    }
}

/// <summary>
/// Adapter over a dynamic named-state registry. Polls the live registry at request time;
/// never snapshots bootstrap configuration.
/// </summary>
internal sealed class ConfiglueDevToolsRegistryEntry<TModel> : IConfiglueDevToolsEntry
    where TModel : IConfiglueFacadeModel<TModel>
{
    private readonly IConfiglueStateRegistry<TModel> _registry;
    private readonly string _stateName;
    private readonly ConfiglueModelSchema _schema;

    public ConfiglueDevToolsRegistryEntry(
        IConfiglueStateRegistry<TModel> registry,
        string stateName
    )
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);
        _stateName = stateName;
        _schema = ConfiglueModelDescriptor<TModel>.Current.Schema;
    }

    public ConfiglueDevToolsStateInfo Info =>
        new(
            _schema.Id,
            _schema.Version,
            _stateName,
            $"{_schema.Id}:{_stateName}",
            typeof(TModel).FullName ?? typeof(TModel).Name
        );

    /// <inheritdoc />
    public Type ModelType => typeof(TModel);

    /// <inheritdoc />
    public object UntypedState => Resolve();

    private IWritableState<TModel> Resolve() => _registry.Get(_stateName);

    private ConfiglueDevToolsEntry<TModel> Bound() => new(Resolve(), _stateName);

    public ValueTask<string> GetStateJsonAsync(CancellationToken cancellationToken) =>
        Bound().GetStateJsonAsync(cancellationToken);

    public string GetSchemaJson() => Bound().GetSchemaJson();

    public ValueTask<string> GetDiagnosticsJsonAsync(CancellationToken cancellationToken) =>
        Bound().GetDiagnosticsJsonAsync(cancellationToken);

    public ValueTask<string> GetStatsJsonAsync(CancellationToken cancellationToken) =>
        Bound().GetStatsJsonAsync(cancellationToken);

    public ValueTask<string> GetEventsJsonAsync(CancellationToken cancellationToken) =>
        Bound().GetEventsJsonAsync(cancellationToken);

    public ValueTask<string> RunCheckAsync(CancellationToken cancellationToken) =>
        Bound().RunCheckAsync(cancellationToken);

    public ValueTask<string> ApplyJsonAsync(
        string incomingJson,
        CancellationToken cancellationToken
    ) => Bound().ApplyJsonAsync(incomingJson, cancellationToken);

    public ValueTask<ConfiglueViewerDocument> GetViewerDocumentAsync(
        ConfiglueDevToolsViewerOptions? options,
        CancellationToken cancellationToken
    ) => Bound().GetViewerDocumentAsync(options, cancellationToken);

    public ValueTask<ConfiglueViewerDelta> GetViewerDeltaAsync(
        long knownVersion,
        ConfiglueDevToolsViewerOptions? options,
        CancellationToken cancellationToken
    ) => Bound().GetViewerDeltaAsync(knownVersion, options, cancellationToken);

    public ConfiglueViewerSchemaSetup GetViewerSchemaSetup(
        ConfiglueDevToolsViewerOptions? options
    ) => Bound().GetViewerSchemaSetup(options);

    public ValueTask<string> GetContributionJsonAsync(
        string memberPath,
        ConfiglueDevToolsViewerOptions? options,
        CancellationToken cancellationToken
    ) => Bound().GetContributionJsonAsync(memberPath, options, cancellationToken);
}

/// <summary>
/// Reads one consistent value+details snapshot for DevTools without extra backend reads.
/// </summary>
internal static class DevToolsSnapshotReader
{
    public static async ValueTask<StateSnapshot<TModel>> ReadAsync<TModel>(
        IWritableState<TModel> state,
        CancellationToken cancellationToken
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state is IConfiglueStateSnapshotRuntime<TModel> runtime)
        {
            return await runtime.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        var value = await state.GetValueAsync(cancellationToken).ConfigureAwait(false);
        return new StateSnapshot<TModel>(value, details: null);
    }
}
