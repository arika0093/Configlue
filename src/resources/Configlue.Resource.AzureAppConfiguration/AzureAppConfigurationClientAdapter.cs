using Azure;
using Azure.Data.AppConfiguration;

namespace Configlue.Resource.AzureAppConfiguration;

/// <summary>Adapts a real <see cref="ConfigurationClient"/> to the internal client contract.</summary>
internal sealed class AzureAppConfigurationClientAdapter : IAppConfigurationClient
{
    private readonly ConfigurationClient _client;

    public AzureAppConfigurationClientAdapter(ConfigurationClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<IReadOnlyList<AppConfigurationEntry>> GetSettingsAsync(
        AppConfigurationSelection selection,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(selection);
        var entries = new List<AppConfigurationEntry>();
        if (selection.SnapshotName is { } snapshotName)
        {
            await foreach (
                var setting in _client.GetConfigurationSettingsForSnapshotAsync(
                    snapshotName,
                    cancellationToken: cancellationToken
                )
            )
            {
                entries.Add(ToEntry(setting));
            }

            return entries;
        }

        var selector = new SettingSelector
        {
            KeyFilter = string.IsNullOrWhiteSpace(selection.KeyFilter) ? "*" : selection.KeyFilter,
            LabelFilter = selection.LabelFilter ?? "\0",
        };
        await foreach (
            var setting in _client.GetConfigurationSettingsAsync(selector, cancellationToken)
        )
        {
            entries.Add(ToEntry(setting));
        }

        return entries;
    }

    public async Task<AppConfigurationEntry?> GetSettingAsync(
        string key,
        string? label,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            var response = await _client
                .GetConfigurationSettingAsync(key, label, cancellationToken)
                .ConfigureAwait(false);
            return ToEntry(response.Value);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<AppConfigurationEntry> SetSettingAsync(
        string key,
        string value,
        string? label,
        string? contentType,
        AppConfigurationWriteCondition condition,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            if (condition.IsMustNotExist)
            {
                var created = new ConfigurationSetting(key, value, label)
                {
                    ContentType = contentType,
                };
                var added = await _client
                    .AddConfigurationSettingAsync(created, cancellationToken)
                    .ConfigureAwait(false);
                return ToEntry(added.Value);
            }

            if (condition.IsMatch)
            {
                var current = await _client
                    .GetConfigurationSettingAsync(key, label, cancellationToken)
                    .ConfigureAwait(false);
                if (
                    !string.Equals(
                        current.Value.ETag.ToString(),
                        condition.ETag,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new StateConflictException(
                        $"The App Configuration setting '{key}' changed after it was read."
                    );
                }

                current.Value.Value = value;
                current.Value.ContentType = contentType;
                var guarded = await _client
                    .SetConfigurationSettingAsync(
                        current.Value,
                        onlyIfUnchanged: true,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);
                return ToEntry(guarded.Value);
            }

            var setting = new ConfigurationSetting(key, value, label) { ContentType = contentType };
            var written = await _client
                .SetConfigurationSettingAsync(
                    setting,
                    onlyIfUnchanged: false,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            return ToEntry(written.Value);
        }
        catch (RequestFailedException exception)
            when (exception.Status is 409 or 412 || exception.ErrorCode is "PreconditionFailed")
        {
            throw new StateConflictException(
                $"The App Configuration setting '{key}' changed after it was read."
            );
        }
    }

    private static AppConfigurationEntry ToEntry(ConfigurationSetting setting) =>
        new(
            setting.Key,
            NormalizeLabel(setting.Label),
            setting.Value,
            setting.ContentType,
            setting.ETag.ToString(),
            setting.LastModified
        );

    private static string? NormalizeLabel(string? label) => label == "\0" ? null : label;
}
