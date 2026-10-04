using System.Text.Json.Serialization;
using Configlue;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.DevTools.Tests;

[ConfiglueModel("devtools-demo", Version = 1)]
public partial class DevToolsDemoSettings
{
    public string Label { get; set; } = "default";

    public int RetryCount { get; set; } = 3;
}

[ConfiglueModel("devtools-secret-demo", Version = 1)]
public partial class DevToolsSecretSettings
{
    public string Host { get; set; } = "localhost";

    [SecretValue]
    public string Password { get; set; } = "";
}

[ConfiglueModel("devtools-named", Version = 1)]
public partial class DevToolsNamedSettings
{
    public string Slot { get; set; } = "empty";
}

[ConfiglueModel("devtools-dynamic", Version = 1)]
public partial class DevToolsDynamicSettings
{
    public string Mood { get; set; } = "calm";
}

[ConfiglueModel("devtools-viewer", Version = 1)]
public partial class DevToolsViewerSettings
{
    public string Theme { get; set; } = "Light";

    public int RetryCount { get; set; } = 3;

    public string? Notes { get; set; }

    public DevToolsViewerDatabase? Database { get; set; } = new();

    public List<string> Tags { get; set; } = ["alpha"];
}

[ConfiglueModel("devtools-viewer-database", Version = 1)]
public partial class DevToolsViewerDatabase
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;

    [SecretValue]
    public string Password { get; set; } = "";
}

[ConfiglueModel("devtools-viewer-naming", Version = 1)]
public partial class DevToolsViewerNamingSettings
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Light";

    public int RetryCount { get; set; } = 3;
}

internal static class DevToolsFixtures
{
    /// <summary>
    /// Builds a memory-backed source through the public <see cref="StateSource{T}"/> surface.
    /// </summary>
    /// <remarks>
    /// One shared store backs reads and writes so edit-session revision conditions hold.
    /// </remarks>
    public static StateSource<TFragment> MemorySource<TFragment>(TFragment initial)
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        var store = new InMemoryStateSource<TFragment>(initial);
        return new StateSource<TFragment>(
            "memory",
            store,
            new StateSourceOptions<TFragment> { Priority = 100, Writer = store }
        );
    }

    public static ConfiglueContext CreateDemoContext(
        string label = "demo",
        int retryCount = 3,
        string? stateName = null
    )
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsDemoSettings>(model =>
        {
            if (stateName is not null)
            {
                model.StateName = stateName;
            }

            model.Sources(sources =>
                sources.Add(
                    MemorySource(
                        DevToolsDemoSettings.Fragment.From(
                            new DevToolsDemoSettings { Label = label, RetryCount = retryCount }
                        )
                    )
                )
            );
        });
        return builder.CreateContext();
    }

    public static ConfiglueContext CreateSecretContext(string password)
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsSecretSettings>(model =>
            model.Sources(sources =>
                sources.Add(
                    MemorySource(
                        DevToolsSecretSettings.Fragment.From(
                            new DevToolsSecretSettings { Host = "db.local", Password = password }
                        )
                    )
                )
            )
        );
        return builder.CreateContext();
    }

    /// <summary>A fragment reader fake that stays on net48-compatible ValueTask construction.</summary>
    internal sealed class UnavailableReader<T> : ISourceReader<T>
    {
        public ValueTask<StateReadResult<T>> ReadAsync(
            Configlue.Resources.ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(StateReadResult<T>.Unavailable());
        }
    }
}
