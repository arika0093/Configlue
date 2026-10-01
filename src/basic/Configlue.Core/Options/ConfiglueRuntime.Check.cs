using Configlue.CompilerServices;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <inheritdoc />
    public ConfiglueCheckOperation Check(CancellationToken cancellationToken = default) =>
        CreateCheckOperation(subject: null, cancellationToken);

    internal ConfiglueCheckOperation CreateCheckOperation(
        IConfiglueSubject? subject,
        CancellationToken cancellationToken
    ) =>
        new(
            (reportSource, token) =>
                subject is null
                    ? RunCheckAsync(reportSource, token)
                    : RunCheckForSubjectAsync(subject, reportSource, token),
            cancellationToken
        );

    private async Task<ConfiglueCheckResult> RunCheckForSubjectAsync(
        IConfiglueSubject subject,
        Action<ConfiglueSourceCheckResult> reportSource,
        CancellationToken cancellationToken
    )
    {
        using var scope = EnterSubject(subject);
        return await RunCheckAsync(reportSource, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConfiglueCheckResult> RunCheckAsync(
        Action<ConfiglueSourceCheckResult> reportSource,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reportSource);
        try
        {
            var resolved = await ResolveCoreAsync(
                    null,
                    cancellationToken,
                    captureContributions: false,
                    observeSource: probe => reportSource(CreateSourceCheckResult(probe))
                )
                .ConfigureAwait(false);
            return CreateCheckResult(resolved.Result);
        }
        catch (ConfiglueValidationException exception)
        {
            return ConfiglueCheckResult.Invalid(exception.Failures);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ConfiglueCheckResult.Faulted(exception);
        }
    }

    private ConfiglueSourceCheckResult CreateSourceCheckResult(ResolvedSourceProbe probe)
    {
        var origin = probe.Result.PhysicalOrigin ?? probe.Source.PhysicalOrigin;
        var details = DescribeSource(
            probe.Source,
            origin,
            DescribeResolution(probe.ResourceContext, probe.ResourceId, origin)
        );
        return new ConfiglueSourceCheckResult(
            details,
            MapCheckStatus(probe.Result.Status, probe.Exception),
            probe.Contributed,
            probe.FallbackContinued,
            exception: probe.Exception
        );
    }

    private static ConfiglueCheckStatus MapCheckStatus(
        StateReadStatus status,
        Exception? exception
    ) =>
        exception is not null
            ? ConfiglueCheckStatus.Faulted
            : status switch
            {
                StateReadStatus.Success => ConfiglueCheckStatus.Success,
                StateReadStatus.NotFound => ConfiglueCheckStatus.NotFound,
                StateReadStatus.Unavailable => ConfiglueCheckStatus.Unavailable,
                StateReadStatus.InvalidPayload => ConfiglueCheckStatus.Invalid,
                _ => ConfiglueCheckStatus.Faulted,
            };

    private static ConfiglueCheckResult CreateCheckResult(StateReadResult<TModel> result) =>
        result.Status switch
        {
            StateReadStatus.Success => ConfiglueCheckResult.Resolved(),
            StateReadStatus.NotFound => ConfiglueCheckResult.NotFound(),
            StateReadStatus.Unavailable => ConfiglueCheckResult.Unavailable(),
            StateReadStatus.InvalidPayload => ConfiglueCheckResult.Invalid(),
            _ => ConfiglueCheckResult.Faulted(
                new InvalidOperationException(
                    $"Configuration state check produced an unexpected status '{result.Status}'."
                )
            ),
        };
}
