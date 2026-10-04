using Amazon.AppConfigData;
using Amazon.AppConfigData.Model;

namespace Configlue.Resource.AwsAppConfig;

/// <summary>Adapts an AWS SDK AppConfig Data client to the session abstraction.</summary>
/// <remarks>The supplied SDK client remains caller-owned and is never disposed by the adapter.</remarks>
internal sealed class AwsAppConfigDataSdkClient : IAwsAppConfigDataClient
{
    private readonly IAmazonAppConfigData _client;

    public AwsAppConfigDataSdkClient(IAmazonAppConfigData client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<string> StartSessionAsync(
        string applicationId,
        string environmentId,
        string configurationProfileId,
        int? requiredMinimumPollIntervalInSeconds,
        CancellationToken cancellationToken
    )
    {
        var request = new StartConfigurationSessionRequest
        {
            ApplicationIdentifier = applicationId,
            EnvironmentIdentifier = environmentId,
            ConfigurationProfileIdentifier = configurationProfileId,
        };
        if (requiredMinimumPollIntervalInSeconds is { } minimum)
        {
            request.RequiredMinimumPollIntervalInSeconds = minimum;
        }

        try
        {
            var response = await _client
                .StartConfigurationSessionAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(response.InitialConfigurationToken))
            {
                throw new AwsAppConfigTransientException(
                    "AppConfig StartConfigurationSession returned an empty initial token."
                );
            }

            return response.InitialConfigurationToken;
        }
        catch (BadRequestException exception)
        {
            throw new AwsAppConfigException(
                "AppConfig StartConfigurationSession was rejected: " + exception.Message,
                exception
            );
        }
        catch (ResourceNotFoundException exception)
        {
            throw new AwsAppConfigException(
                "The AppConfig application, environment, or configuration profile was not found: "
                    + exception.Message,
                exception
            );
        }
        catch (ThrottlingException exception)
        {
            throw new AwsAppConfigTransientException(
                "AppConfig StartConfigurationSession was throttled.",
                exception
            );
        }
        catch (InternalServerException exception)
        {
            throw new AwsAppConfigTransientException(
                "AppConfig StartConfigurationSession failed on the service.",
                exception
            );
        }
        catch (AmazonAppConfigDataException exception)
        {
            throw new AwsAppConfigTransientException(
                "AppConfig StartConfigurationSession failed: " + exception.Message,
                exception
            );
        }
    }

    public async Task<AppConfigPollResult> GetLatestAsync(
        string configurationToken,
        CancellationToken cancellationToken
    )
    {
        GetLatestConfigurationResponse response;
        try
        {
            response = await _client
                .GetLatestConfigurationAsync(
                    new GetLatestConfigurationRequest { ConfigurationToken = configurationToken },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (BadRequestException exception)
        {
            // Expired or otherwise unusable tokens are recovered by restarting the session once.
            throw new AwsAppConfigSessionExpiredException(
                "The AppConfig configuration token is no longer valid.",
                exception
            );
        }
        catch (ResourceNotFoundException exception)
        {
            throw new AwsAppConfigSessionExpiredException(
                "The AppConfig session is no longer known to the service.",
                exception
            );
        }
        catch (ThrottlingException exception)
        {
            throw new AwsAppConfigTransientException(
                "AppConfig GetLatestConfiguration was throttled.",
                exception
            );
        }
        catch (InternalServerException exception)
        {
            throw new AwsAppConfigTransientException(
                "AppConfig GetLatestConfiguration failed on the service.",
                exception
            );
        }
        catch (AmazonAppConfigDataException exception)
        {
            throw new AwsAppConfigTransientException(
                "AppConfig GetLatestConfiguration failed: " + exception.Message,
                exception
            );
        }

        if (string.IsNullOrEmpty(response.NextPollConfigurationToken))
        {
            throw new AwsAppConfigTransientException(
                "AppConfig GetLatestConfiguration returned an empty next token."
            );
        }

        ReadOnlyMemory<byte> configuration = response.Configuration is { Length: > 0 } content
            ? content.ToArray()
            : default;
        var interval =
            response.NextPollIntervalInSeconds is { } seconds && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
        return new AppConfigPollResult(
            configuration,
            response.NextPollConfigurationToken,
            interval,
            string.IsNullOrEmpty(response.ContentType) ? null : response.ContentType,
            string.IsNullOrEmpty(response.VersionLabel) ? null : response.VersionLabel
        );
    }
}
