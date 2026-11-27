using Microsoft.Extensions.Options;
using Pulse.Agent.Windows.Collectors;
using Pulse.Agent.Windows.Services;

namespace Pulse.Agent.Windows;

public class Worker(
    ICpuTemperatureSource cpuTemperature,
    ITelemetryPublisher telemetry,
    IOptions<CpuTemperatureOptions> options,
    TimeProvider timeProvider,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds);
        using var timer = new PeriodicTimer(interval, timeProvider);
        var reportUnavailable = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (cpuTemperature.ReadTemperatureCelsius() is float temperature)
            {
                telemetry.PublishCpuTemperature(temperature, timeProvider.GetUtcNow());
                logger.LogInformation("CPU temperature: {TemperatureCelsius:F1} °C", temperature);
                reportUnavailable = true;
            }
            else if (reportUnavailable)
            {
                logger.LogWarning("CPU temperature is unavailable");
                reportUnavailable = false;
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }
}
