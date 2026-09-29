using Configlue;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Example.WorkerService;

internal sealed class Worker(
    IWritableState<SampleSetting> settings,
    IConfiglueEditSessions<SampleSetting> editSessions,
    ILogger<Worker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = settings.OnChange(value =>
            logger.LogInformation(
                "Settings changed: Name={Name}, RunCount={RunCount}",
                value.Name,
                value.RunCount
            )
        );
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (!stoppingToken.IsCancellationRequested)
        {
            var current = await settings.GetValueAsync(stoppingToken);
            logger.LogInformation(
                "Current settings: Name={Name}, RunCount={RunCount}",
                current.Name,
                current.RunCount
            );

            using var edit = await editSessions.OpenEditSessionAsync(stoppingToken);
            edit.Value.RunCount++;
            await edit.CommitAsync(stoppingToken);
            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}
