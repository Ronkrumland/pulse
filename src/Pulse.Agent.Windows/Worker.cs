using Microsoft.Extensions.Options;
using Pulse.Agent.Windows.Collectors;
using Pulse.Agent.Windows.Services;

namespace Pulse.Agent.Windows;

public class Worker(
    ICpuTemperatureSource cpuTemperature,
    IGpuSource gpuSource,
    IMemorySource memorySource,
    ITelemetryPublisher telemetry,
    IOptions<TelemetryOptions> options,
    TimeProvider timeProvider,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds);
        using var timer = new PeriodicTimer(interval, timeProvider);
        var reportCpuUnavailable = true;
        var reportGpuTemperatureUnavailable = true;
        var reportGpuMemoryUnavailable = true;
        var reportMemoryUnavailable = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            var cpuCelsius = cpuTemperature.ReadTemperatureCelsius();
            var gpu = gpuSource.Read();
            var memory = memorySource.ReadMemory();

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

            if (gpu.TemperatureCelsius is float gpuTemp)
            {
                logger.LogInformation("GPU temperature: {GpuTemperatureCelsius:F1} °C", gpuTemp);
                reportGpuTemperatureUnavailable = true;
            }
            else if (reportGpuTemperatureUnavailable)
            {
                logger.LogWarning("GPU temperature is unavailable");
                reportGpuTemperatureUnavailable = false;
            }

            if (memory?.UsedMegabytes is float memoryUsed)
            {
                if (memory.TotalMegabytes is float memoryTotal)
                {
                    logger.LogInformation("System memory: {MemoryUsedMegabytes:F0} / {MemoryTotalMegabytes:F0} MB",
                        memoryUsed, memoryTotal);
                }
                else
                {
                    logger.LogInformation("System memory used: {MemoryUsedMegabytes:F0} MB", memoryUsed);
                }

                reportMemoryUnavailable = true;
            }
            else if (reportMemoryUnavailable)
            {
                logger.LogWarning("System memory is unavailable");
                reportMemoryUnavailable = false;
            }

            if (gpu.MemoryUsedMegabytes is float gpuMemoryUsed)
            {
                if (gpu.MemoryTotalMegabytes is float gpuMemoryTotal)
                {
                    logger.LogInformation("GPU memory: {GpuMemoryUsedMegabytes:F0} / {GpuMemoryTotalMegabytes:F0} MB",
                        gpuMemoryUsed, gpuMemoryTotal);
                }
                else
                {
                    logger.LogInformation("GPU memory used: {GpuMemoryUsedMegabytes:F0} MB", gpuMemoryUsed);
                }

                reportGpuMemoryUnavailable = true;
            }
            else if (reportGpuMemoryUnavailable)
            {
                logger.LogWarning("GPU memory is unavailable");
                reportGpuMemoryUnavailable = false;
            }

            if (cpuCelsius is not null
                || gpu.TemperatureCelsius is not null
                || memory?.UsedMegabytes is not null
                || gpu.MemoryUsedMegabytes is not null)
            {
                telemetry.PublishTelemetry(new TelemetrySample(
                    timeProvider.GetUtcNow(),
                    cpuCelsius,
                    gpu.TemperatureCelsius,
                    memory?.UsedMegabytes,
                    memory?.TotalMegabytes,
                    gpu.MemoryUsedMegabytes,
                    gpu.MemoryTotalMegabytes));
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }
}
