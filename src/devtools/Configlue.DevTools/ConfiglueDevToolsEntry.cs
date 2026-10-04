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

    ValueTask<string> GetStateJsonAsync(CancellationToken cancellationToken);

    string GetSchemaJson();

    ValueTask<string> GetDiagnosticsJsonAsync(CancellationToken cancellationToken);

    ValueTask<string> RunCheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies a canonical JSON payload through the normal edit-session write routing.
    /// Returns a summary; throws when nothing is bound or the payload is invalid.
    /// </summary>
    ValueTask<string> ApplyJsonAsync(string incomingJson, CancellationToken cancellationToken);
}

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
        var payload = new
        {
            stateName = topology.StateName,
            modelId = runtime.ModelId,
            modelVersion = runtime.ModelVersion,
            defaultWriteSourceId = topology.DefaultWriteSourceId?.ToString(),
            defaultWriteSourceIsInferred = topology.DefaultWriteSourceIsInferred,
            sources = topology.Sources.Select(static source => new
            {
                id = source.Id.ToString(),
                priority = source.Priority,
                canRead = source.CanRead,
                canWrite = source.CanWrite,
                canWatch = source.CanWatch,
                isActive = source.IsActive,
                physicalOrigin = source.PhysicalOrigin,
            }),
            runtimeSources = runtime.Sources.Select(static source => new
            {
                id = source.Id.ToString(),
                kind = source.Kind,
                isActive = source.IsActive,
                canRead = source.CanRead,
                canWrite = source.CanWrite,
                canWatch = source.CanWatch,
                isWatching = source.IsWatching,
                lastSuccessfulRead = source.LastSuccessfulRead,
                lastWatchSignal = source.LastWatchSignal,
            }),
            lastResolution = runtime.LastResolution?.Kind.ToString(),
            lastReload = runtime.LastReload?.Kind.ToString(),
            lastWrite = runtime.LastWrite?.Kind.ToString(),
            lastMigration = runtime.LastMigration?.Kind.ToString(),
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

    private IWritableState<TModel> Resolve() => _registry.Get(_stateName);

    private ConfiglueDevToolsEntry<TModel> Bound() => new(Resolve(), _stateName);

    public ValueTask<string> GetStateJsonAsync(CancellationToken cancellationToken) =>
        Bound().GetStateJsonAsync(cancellationToken);

    public string GetSchemaJson() => Bound().GetSchemaJson();

    public ValueTask<string> GetDiagnosticsJsonAsync(CancellationToken cancellationToken) =>
        Bound().GetDiagnosticsJsonAsync(cancellationToken);

    public ValueTask<string> RunCheckAsync(CancellationToken cancellationToken) =>
        Bound().RunCheckAsync(cancellationToken);

    public ValueTask<string> ApplyJsonAsync(
        string incomingJson,
        CancellationToken cancellationToken
    ) => Bound().ApplyJsonAsync(incomingJson, cancellationToken);
}
