using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.Source.Environment;

namespace Configlue.Source.Presets;

/// <summary>Configures one environment source.</summary>
public sealed class CommonEnvironmentSourceBuilder
{
    private readonly CommonSourceBuilder.SourceDeclaration _declaration;
    private readonly string _prefix;
    private Func<IEnumerable<KeyValuePair<string, string?>>>? _environmentVariables;
    private Func<string, Type, object?>? _valueParser;
    private JsonSerializerOptions? _jsonOptions;

    internal CommonEnvironmentSourceBuilder(
        CommonSourceBuilder.SourceDeclaration declaration,
        string prefix
    )
    {
        _declaration = declaration;
        _prefix = prefix;
        UpdateRegistration();
    }

    /// <summary>Sets the environment variable provider, useful for tests and custom hosts.</summary>
    public CommonEnvironmentSourceBuilder EnvironmentVariables(
        Func<IEnumerable<KeyValuePair<string, string?>>> environmentVariables
    )
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(environmentVariables);
        _environmentVariables = environmentVariables;
        UpdateRegistration();
        return this;
    }

    /// <summary>Sets the scalar parser used by this environment source.</summary>
    public CommonEnvironmentSourceBuilder ValueParser(Func<string, Type, object?> valueParser)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(valueParser);
        _valueParser = valueParser;
        UpdateRegistration();
        return this;
    }

    /// <summary>Sets JSON options used by this environment source.</summary>
    public CommonEnvironmentSourceBuilder JsonSerializerOptions(JsonSerializerOptions options)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(options);
        _jsonOptions = options;
        UpdateRegistration();
        return this;
    }

    /// <summary>Overrides the fixed environment-layer priority.</summary>
    public CommonEnvironmentSourceBuilder Priority(int priority)
    {
        EnsureMutable();
        _declaration.Priority = priority;
        return this;
    }

    private void UpdateRegistration()
    {
        _declaration.Register = (sources, priority, _) =>
            sources.FromEnvironment(
                new EnvironmentSourceOptions
                {
                    Prefix = _prefix,
                    Priority = priority,
                    FallbackCondition = StateFallbackCondition.NotFound,
                    EnvironmentVariables = _environmentVariables,
                    ValueParser = _valueParser,
                    JsonSerializerOptions = _jsonOptions,
                }
            );
    }

    private void EnsureMutable() => _declaration.EnsureMutable();
}

/// <summary>Configures the priority for a custom source inserted between built-in layers.</summary>
public sealed class CommonCustomSourceBuilder
{
    private readonly CommonSourceBuilder.SourceDeclaration _declaration;

    internal CommonCustomSourceBuilder(CommonSourceBuilder.SourceDeclaration declaration)
    {
        _declaration = declaration;
    }

    /// <summary>Overrides the automatically assigned insertion priority.</summary>
    public CommonCustomSourceBuilder Priority(int priority)
    {
        _declaration.EnsureMutable();
        _declaration.Priority = priority;
        return this;
    }
}

/// <summary>Registers the common source preset for multiple model registrations.</summary>
public static class CommonSourceBuilderExtensions
{
    /// <summary>Configures common sources and adds models within the same scope.</summary>
    public static void UseCommonSources(
        this ConfiglueBuilder configlue,
        Action<CommonSourceBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configlue);
        ArgumentNullException.ThrowIfNull(configure);
        var commonSources = new CommonSourceBuilder(configlue);
        configure(commonSources);
        commonSources.EnsureHasModel();
    }
}
