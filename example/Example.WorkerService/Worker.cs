using Configlue;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Example.WorkerService;

internal sealed class Worker(IWritableOptions<SampleSetting> settings, ILogger<Worker> logger)
    : BackgroundService
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

            await settings.SaveAsync(value => value.RunCount++, stoppingToken);
            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}
