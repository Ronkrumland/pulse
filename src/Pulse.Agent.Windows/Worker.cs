using Microsoft.Extensions.Options;
using Pulse.Agent.Windows.Collectors;
using Pulse.Agent.Windows.Services;

namespace Pulse.Agent.Windows;

public class Worker(
    ICpuTemperatureSource cpuTemperature,
    IGpuTemperatureSource gpuTemperature,
    ITelemetryPublisher telemetry,
    IOptions<CpuTemperatureOptions> options,
    TimeProvider timeProvider,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds);
        using var timer = new PeriodicTimer(interval, timeProvider);
        var reportCpuUnavailable = true;
        var reportGpuUnavailable = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            var cpuCelsius = cpuTemperature.ReadTemperatureCelsius();
            var gpuCelsius = gpuTemperature.ReadTemperatureCelsius();

            if (cpuCelsius is float cpu)
            {
                logger.LogInformation("CPU temperature: {CpuTemperatureCelsius:F1} °C", cpu);
                reportCpuUnavailable = true;
            }
            else if (reportCpuUnavailable)
            {
                logger.LogWarning("CPU temperature is unavailable");
                reportCpuUnavailable = false;
            }

            if (gpuCelsius is float gpu)
            {
                logger.LogInformation("GPU temperature: {GpuTemperatureCelsius:F1} °C", gpu);
                reportGpuUnavailable = true;
            }
            else if (reportGpuUnavailable)
            {
                logger.LogWarning("GPU temperature is unavailable");
                reportGpuUnavailable = false;
            }

            if (cpuCelsius is not null || gpuCelsius is not null)
            {
                telemetry.PublishTelemetry(cpuCelsius, gpuCelsius, timeProvider.GetUtcNow());
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }
}
