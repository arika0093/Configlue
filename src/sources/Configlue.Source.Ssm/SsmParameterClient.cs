using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Configlue.Source.Ssm;

/// <summary>Value-free parameter data used for mapping, revision, and provenance.</summary>
internal sealed record SsmParameterData
{
    public required string Name { get; init; }

    public string? Value { get; init; }

    public required string Type { get; init; }

    public required long Version { get; init; }

    public string? Arn { get; init; }

    public string? Tier { get; init; }

    public string? DataType { get; init; }
}

internal sealed record SsmParameterPage(
    IReadOnlyList<SsmParameterData> Parameters,
    string? NextToken
);

internal sealed record SsmPutResult(long Version);

internal interface ISsmParameterClient
{
    Task<SsmParameterPage> GetParametersByPathAsync(
        string path,
        bool recursive,
        bool withDecryption,
        string? nextToken,
        int? maxResults,
        CancellationToken cancellationToken
    );

    Task<SsmPutResult> PutParameterAsync(
        string name,
        string value,
        string type,
        string? keyId,
        bool overwrite,
        string? tier,
        string? dataType,
        CancellationToken cancellationToken
    );
}

internal sealed class SsmAwsParameterClient : ISsmParameterClient
{
    private readonly IAmazonSimpleSystemsManagement _client;

    public SsmAwsParameterClient(IAmazonSimpleSystemsManagement client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<SsmParameterPage> GetParametersByPathAsync(
        string path,
        bool recursive,
        bool withDecryption,
        string? nextToken,
        int? maxResults,
        CancellationToken cancellationToken
    )
    {
        var request = new GetParametersByPathRequest
        {
            Path = path,
            Recursive = recursive,
            WithDecryption = withDecryption,
            NextToken = nextToken,
        };
        if (maxResults.HasValue)
        {
            request.MaxResults = maxResults.Value;
        }

        var response = await _client
            .GetParametersByPathAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var parameters = new List<SsmParameterData>(response.Parameters?.Count ?? 0);
        if (response.Parameters is not null)
        {
            foreach (var parameter in response.Parameters)
            {
                parameters.Add(
                    new SsmParameterData
                    {
                        Name = parameter.Name ?? string.Empty,
                        Value = parameter.Value,
                        Type = parameter.Type?.Value ?? ParameterType.String.Value,
                        Version = parameter.Version ?? 0,
                        Arn = parameter.ARN,
                        Tier = null,
                        DataType = parameter.DataType,
                    }
                );
            }
        }

        return new SsmParameterPage(parameters, response.NextToken);
    }

    public async Task<SsmPutResult> PutParameterAsync(
        string name,
        string value,
        string type,
        string? keyId,
        bool overwrite,
        string? tier,
        string? dataType,
        CancellationToken cancellationToken
    )
    {
        var request = new PutParameterRequest
        {
            Name = name,
            Value = value,
            Type = ParameterType.FindValue(type),
            Overwrite = overwrite,
        };
        if (keyId is not null)
        {
            request.KeyId = keyId;
        }

        if (tier is not null)
        {
            request.Tier = ParameterTier.FindValue(tier);
        }

        if (dataType is not null)
        {
            request.DataType = dataType;
        }

        var response = await _client
            .PutParameterAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return new SsmPutResult(response.Version ?? 0);
    }
}

internal static class SsmThrottling
{
    private static readonly HashSet<string> ThrottlingCodes = new(StringComparer.Ordinal)
    {
        "Throttling",
        "ThrottlingException",
        "Throttled",
        "ThrottledException",
        "TooManyRequestsException",
        "ProvisionedThroughputExceededException",
        "RequestLimitExceeded",
        "RequestThrottled",
        "PriorRequestNotComplete",
        "TooManyUpdates",
    };

    public static bool IsThrottling(Exception exception)
    {
        if (exception is ThrottlingException)
        {
            return true;
        }

        if (exception is ParameterNotFoundException)
        {
            return false;
        }

        if (exception is AmazonServiceException service)
        {
            if (service.ErrorCode is not null && ThrottlingCodes.Contains(service.ErrorCode))
            {
                return true;
            }

            if (
                service.ErrorCode is not null
                && service.ErrorCode.Contains("Throttl", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }

            return service.StatusCode
                is (System.Net.HttpStatusCode)429
                    or System.Net.HttpStatusCode.InternalServerError
                    or System.Net.HttpStatusCode.BadGateway
                    or System.Net.HttpStatusCode.ServiceUnavailable
                    or System.Net.HttpStatusCode.GatewayTimeout;
        }

        return false;
    }

    public static bool IsMissing(Exception exception) =>
        exception is ParameterNotFoundException
        || (
            exception is AmazonServiceException service
            && string.Equals(service.ErrorCode, "ParameterNotFound", StringComparison.Ordinal)
        );
}
