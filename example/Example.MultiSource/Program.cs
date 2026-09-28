using System.Text.Json;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Resource.Http;
using Example.MultiSource;

string? requestedName = null;
if (args.Length > 0)
{
    if (args.Length != 2 || args[0] != "--set-name")
    {
        Console.Error.WriteLine("Usage: Example.MultiSource [--set-name <name>]");
        return 2;
    }

    requestedName = args[1];
}

Uri? policyUri = null;
var configuredPolicyUrl = Environment.GetEnvironmentVariable("CONFIGLUE_POLICY_URL");
if (!string.IsNullOrWhiteSpace(configuredPolicyUrl))
{
    if (
        !Uri.TryCreate(configuredPolicyUrl, UriKind.Absolute, out var parsedPolicyUri)
        || (
            parsedPolicyUri.Scheme != Uri.UriSchemeHttp
            && parsedPolicyUri.Scheme != Uri.UriSchemeHttps
        )
    )
    {
        Console.Error.WriteLine("CONFIGLUE_POLICY_URL must be an absolute HTTP or HTTPS URL.");
        return 2;
    }

    policyUri = parsedPolicyUri;
}

var explicitPath = Path.Combine(AppContext.BaseDirectory, "usersettings.json");
var localPath = Path.Combine(AppContext.BaseDirectory, "localsettings.json");
var globalPath = Path.Combine(AppContext.BaseDirectory, "globalsettings.json");
using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
using var explicitResource = new FileResource(explicitPath);
using var localResource = new FileResource(localPath);
using var globalResource = new FileResource(globalPath);

var codec = new JsonStateCodec<SampleSetting.Fragment>(
    new JsonSerializerOptions { WriteIndented = true }
);

// Higher-priority fragments override matching members while missing members fall through.
var sources = new List<StateSource<SampleSetting.Fragment>>
{
    SerializedStateSource.FromResource<SampleSetting.Fragment>(
        "explicit",
        explicitResource,
        codec,
        priority: 300,
        fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable,
        physicalOrigin: explicitPath
    ),
    ReadOnlyFileSource("local", localResource, codec, 200, localPath),
    ReadOnlyFileSource("global", globalResource, codec, 100, globalPath),
};

if (policyUri is not null)
{
    // The remote fragment can contain only Policy; the file sources provide other members.
    var remotePolicy = new HttpResourceReader(httpClient, policyUri);
    sources.Add(
        SerializedStateSource.FromResource<SampleSetting.Fragment>(
            "http-policy",
            remotePolicy,
            codec,
            priority: 400,
            fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable,
            watcher: remotePolicy,
            physicalOrigin: policyUri.ToString()
        )
    );
    Console.WriteLine($"HTTP policy source: {policyUri}");
}
else
{
    Console.WriteLine("HTTP policy source is disabled; using the file layers.");
}

// Writes always target the explicit file; local and global files remain read-only fallbacks.
await using var options = new ConfiglueOptions<SampleSetting, SampleSetting.Fragment>(
    new StateSourceSet<SampleSetting.Fragment>(sources),
    StateWriteRoute.To("explicit"),
    onChangeDebounce: TimeSpan.Zero
);
var writable = (IConfiglueOptions<SampleSetting>)options;
var current = await writable.GetValueAsync();
PrintSettings(current);

if (requestedName is not null)
{
    var explicitSource = writable.Source(SourceKey<SampleSetting>.Named("explicit"));
    await explicitSource.ReplaceAsync(patch => patch.Name = requestedName);
    Console.WriteLine("Saved to the explicit settings file.");
    PrintSettings(await writable.GetValueAsync());
}

Console.WriteLine($"Explicit settings file: {explicitPath}");
return 0;

static StateSource<SampleSetting.Fragment> ReadOnlyFileSource(
    string id,
    FileResource resource,
    JsonStateCodec<SampleSetting.Fragment> codec,
    int priority,
    string physicalOrigin
) =>
    new(
        id,
        new SerializedStateReader<SampleSetting.Fragment>(resource, codec),
        priority,
        StateFallbackCondition.NotFoundOrUnavailable,
        watcher: resource,
        physicalOrigin: physicalOrigin,
        resourceId: resource.ResourceId
    );

static void PrintSettings(SampleSetting settings)
{
    Console.WriteLine($"Name: {settings.Name}");
    Console.WriteLine($"Policy: {settings.Policy}");
    Console.WriteLine($"Region: {settings.Region}");
}
