using System.Text.Json;
using Configlue.Source.Presets;

namespace Configlue.Source.Http;

/// <summary>Configures one HTTP policy source as an opt-in common preset integration.</summary>
/// <remarks>
/// HTTP remains outside the default <c>Configlue</c> package. Install
/// <c>Configlue.Source.Http</c> and use these extensions to add a read-only
/// JSON-over-HTTP policy layer to <c>UseCommonSources</c>.
/// </remarks>
public sealed class CommonHttpSourceBuilder
{
    private readonly CommonSourceBuilder.SourceDeclaration _declaration;
    private readonly string _endpoint;
    private readonly HttpClient? _client;
    private readonly Func<IServiceProvider?, HttpClient>? _clientFactory;
    private readonly string _id;
    private JsonSerializerOptions? _serializerOptions;
    private StateFallbackCondition _fallbackCondition = StateFallbackCondition.NotFound;

    internal CommonHttpSourceBuilder(
        CommonSourceBuilder.SourceDeclaration declaration,
        string endpoint,
        HttpClient client,
        string id
    )
    {
        _declaration = declaration;
        _endpoint = endpoint;
        _client = client;
        _id = id;
        UpdateRegistration();
    }

    internal CommonHttpSourceBuilder(
        CommonSourceBuilder.SourceDeclaration declaration,
        string endpoint,
        Func<IServiceProvider?, HttpClient> clientFactory,
        string id
    )
    {
        _declaration = declaration;
        _endpoint = endpoint;
        _clientFactory = clientFactory;
        _id = id;
        UpdateRegistration();
    }

    /// <summary>Sets JSON serialization options for the HTTP source.</summary>
    public CommonHttpSourceBuilder SerializerOptions(JsonSerializerOptions options)
    {
        _declaration.EnsureMutable();
        ArgumentNullException.ThrowIfNull(options);
        _serializerOptions = options;
        UpdateRegistration();
        return this;
    }

    /// <summary>Sets which HTTP read statuses allow lower-priority sources to be tried.</summary>
    public CommonHttpSourceBuilder FallbackCondition(StateFallbackCondition fallbackCondition)
    {
        _declaration.EnsureMutable();
        _fallbackCondition = fallbackCondition;
        UpdateRegistration();
        return this;
    }

    /// <summary>Overrides the fixed HTTP-layer priority.</summary>
    public CommonHttpSourceBuilder Priority(int priority)
    {
        _declaration.EnsureMutable();
        _declaration.Priority = priority;
        return this;
    }

    private void UpdateRegistration()
    {
        _declaration.Register = (sources, priority, _) =>
            sources.FromHttpState(
                new HttpStateSourceOptions
                {
                    Id = _id,
                    EndPoint = _endpoint,
                    Client = _client,
                    ClientFactory = _clientFactory,
                    Priority = priority,
                    FallbackCondition = _fallbackCondition,
                    Writable = false,
                    SerializerOptions = _serializerOptions,
                }
            );
    }
}

/// <summary>Adds the opt-in HTTP policy layer to the common source preset.</summary>
public static class CommonHttpSourcePresetExtensions
{
    /// <summary>Registers a read-only JSON-over-HTTP source.</summary>
    public static CommonHttpSourceBuilder WithHttpPolicy(
        this CommonSourceBuilder sources,
        string endpoint,
        HttpClient client,
        string id = "common.http"
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(client);
        var declaration = sources.AddPresetDeclaration(CommonSourceLayer.Http);
        return new CommonHttpSourceBuilder(declaration, endpoint, client, id);
    }

    /// <summary>Registers a read-only JSON-over-HTTP source using a provider-aware client factory.</summary>
    public static CommonHttpSourceBuilder WithHttpPolicy(
        this CommonSourceBuilder sources,
        string endpoint,
        Func<IServiceProvider?, HttpClient> clientFactory,
        string id = "common.http"
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(clientFactory);
        var declaration = sources.AddPresetDeclaration(CommonSourceLayer.Http);
        return new CommonHttpSourceBuilder(declaration, endpoint, clientFactory, id);
    }
}
