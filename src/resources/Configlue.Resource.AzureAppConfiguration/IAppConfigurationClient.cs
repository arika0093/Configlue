namespace Configlue.Resource.AzureAppConfiguration;

/// <summary>Internal selection describing one fetch from App Configuration.</summary>
internal sealed class AppConfigurationSelection
{
    public AppConfigurationSelection(string? keyFilter, string? labelFilter, string? snapshotName)
    {
        KeyFilter = keyFilter;
        LabelFilter = labelFilter;
        SnapshotName = snapshotName;
    }

    public string? KeyFilter { get; }

    public string? LabelFilter { get; }

    public string? SnapshotName { get; }
}

/// <summary>Internal write precondition for one App Configuration key.</summary>
internal readonly record struct AppConfigurationWriteCondition
{
    private readonly byte _kind;

    private AppConfigurationWriteCondition(byte kind, string? eTag)
    {
        _kind = kind;
        ETag = eTag;
    }

    public string? ETag { get; }

    public bool IsNone => _kind == 0;

    public bool IsMustNotExist => _kind == 1;

    public bool IsMatch => _kind == 2;

    public static AppConfigurationWriteCondition None => default;

    public static AppConfigurationWriteCondition MustNotExist => new(1, null);

    public static AppConfigurationWriteCondition Match(string eTag)
    {
        ArgumentNullException.ThrowIfNull(eTag);
        return new(2, eTag);
    }
}

/// <summary>Abstraction over <c>ConfigurationClient</c> so tests can use in-memory fakes.</summary>
internal interface IAppConfigurationClient
{
    Task<IReadOnlyList<AppConfigurationEntry>> GetSettingsAsync(
        AppConfigurationSelection selection,
        CancellationToken cancellationToken
    );

    Task<AppConfigurationEntry?> GetSettingAsync(
        string key,
        string? label,
        CancellationToken cancellationToken
    );

    Task<AppConfigurationEntry> SetSettingAsync(
        string key,
        string value,
        string? label,
        string? contentType,
        AppConfigurationWriteCondition condition,
        CancellationToken cancellationToken
    );
}
