using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Pulse.Agent.Windows;
using Pulse.Agent.Windows.Collectors;
using Pulse.Agent.Windows.Services;
using Xunit;

namespace Pulse.Agent.Tests;

public class WorkerTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public async Task PollsImmediatelyAndAtTheConfiguredIntervalUntilStopped(int intervalSeconds)
    {
        var clock = new FakeTimeProvider();
        var start = clock.GetUtcNow();
        var cpuSensor = new CpuTemperatureSource(42.5f);
        var gpuSensor = new GpuTemperatureSource(61.0f);
        var logger = new SampleLogger(clock);
        var options = Options.Create(new CpuTemperatureOptions { PollingIntervalSeconds = intervalSeconds });
        var publisher = Substitute.For<ITelemetryPublisher>();
        using var worker = new Worker(cpuSensor, gpuSensor, publisher, options, clock, logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var millisecond = TimeSpan.FromMilliseconds(1);

        await worker.StartAsync(timeout.Token);
        var firstCpu = await logger.Samples.Reader.ReadAsync(timeout.Token);
        var firstGpu = await logger.Samples.Reader.ReadAsync(timeout.Token);
        Assert.Equal(start, firstCpu.Time);
        Assert.Equal(LogLevel.Information, firstCpu.Level);
        Assert.Equal(42.5f, firstCpu.CpuTemperature);
        Assert.Equal(start, firstGpu.Time);
        Assert.Equal(LogLevel.Information, firstGpu.Level);
        Assert.Equal(61.0f, firstGpu.GpuTemperature);

        clock.Advance(interval - millisecond);
        Assert.False(logger.Samples.Reader.TryRead(out _));
        clock.Advance(millisecond);
        Assert.Equal(start + interval, (await logger.Samples.Reader.ReadAsync(timeout.Token)).Time);
        Assert.Equal(start + interval, (await logger.Samples.Reader.ReadAsync(timeout.Token)).Time);

        clock.Advance(interval);
        Assert.Equal(start + interval + interval, (await logger.Samples.Reader.ReadAsync(timeout.Token)).Time);
        Assert.Equal(start + interval + interval, (await logger.Samples.Reader.ReadAsync(timeout.Token)).Time);

        await worker.StopAsync(timeout.Token);
        clock.Advance(interval);
        Assert.False(logger.Samples.Reader.TryRead(out _));
        Assert.Equal(3, cpuSensor.ReadCount);
        Assert.Equal(3, gpuSensor.ReadCount);
        publisher.Received(1).PublishTelemetry(42.5f, 61.0f, start);
        publisher.Received(1).PublishTelemetry(42.5f, 61.0f, start + interval);
        publisher.Received(1).PublishTelemetry(42.5f, 61.0f, start + interval + interval);
    }

    [Fact]
    public async Task ContinuesPollingWhenTemperaturesAreUnavailableOrPartiallyAvailable()
    {
        var clock = new FakeTimeProvider();
        var cpuSensor = new CpuTemperatureSource(null);
        var gpuSensor = new GpuTemperatureSource(null);
        var logger = new SampleLogger(clock);
        var options = new CpuTemperatureOptions();
        var publisher = Substitute.For<ITelemetryPublisher>();
        using var worker = new Worker(cpuSensor, gpuSensor, publisher, Options.Create(options), clock, logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var interval = TimeSpan.FromSeconds(options.PollingIntervalSeconds);

        await worker.StartAsync(timeout.Token);
        var cpuUnavailable = await logger.Samples.Reader.ReadAsync(timeout.Token);
        var gpuUnavailable = await logger.Samples.Reader.ReadAsync(timeout.Token);
        Assert.Equal(LogLevel.Warning, cpuUnavailable.Level);
        Assert.Null(cpuUnavailable.CpuTemperature);
        Assert.Equal(LogLevel.Warning, gpuUnavailable.Level);
        Assert.Null(gpuUnavailable.GpuTemperature);

        gpuSensor.Temperature = 61f;
        clock.Advance(interval);
        var gpuOnly = await logger.Samples.Reader.ReadAsync(timeout.Token);
        Assert.Equal(LogLevel.Information, gpuOnly.Level);
        Assert.Equal(61f, gpuOnly.GpuTemperature);

        cpuSensor.Temperature = 58f;
        clock.Advance(interval);
        var cpuRecovered = await logger.Samples.Reader.ReadAsync(timeout.Token);
        var gpuContinued = await logger.Samples.Reader.ReadAsync(timeout.Token);
        Assert.Equal(LogLevel.Information, cpuRecovered.Level);
        Assert.Equal(58f, cpuRecovered.CpuTemperature);
        Assert.Equal(LogLevel.Information, gpuContinued.Level);
        Assert.Equal(61f, gpuContinued.GpuTemperature);

        cpuSensor.Temperature = null;
        gpuSensor.Temperature = null;
        clock.Advance(interval);
        Assert.Equal(LogLevel.Warning, (await logger.Samples.Reader.ReadAsync(timeout.Token)).Level);
        Assert.Equal(LogLevel.Warning, (await logger.Samples.Reader.ReadAsync(timeout.Token)).Level);

        await worker.StopAsync(timeout.Token);
        publisher.Received(2).PublishTelemetry(Arg.Any<float?>(), Arg.Any<float?>(), Arg.Any<DateTimeOffset>());
        publisher.Received(1).PublishTelemetry(null, 61f, gpuOnly.Time);
        publisher.Received(1).PublishTelemetry(58f, 61f, cpuRecovered.Time);
    }

    private sealed class CpuTemperatureSource(float? temperature) : ICpuTemperatureSource
    {
        public float? Temperature { get; set; } = temperature;
        public int ReadCount { get; private set; }

        public float? ReadTemperatureCelsius()
        {
            ReadCount++;
            return Temperature;
        }
    }

    private sealed class GpuTemperatureSource(float? temperature) : IGpuTemperatureSource
    {
        public float? Temperature { get; set; } = temperature;
        public int ReadCount { get; private set; }

        public float? ReadTemperatureCelsius()
        {
            ReadCount++;
            return Temperature;
        }
    }

    private sealed class SampleLogger(TimeProvider clock) : ILogger<Worker>
    {
        public Channel<(DateTimeOffset Time, LogLevel Level, float? CpuTemperature, float? GpuTemperature)> Samples { get; } =
            Channel.CreateUnbounded<(DateTimeOffset, LogLevel, float?, float?)>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = (IReadOnlyList<KeyValuePair<string, object?>>)(object)state!;
            var cpuTemperature = fields.FirstOrDefault(field => field.Key == "CpuTemperatureCelsius").Value as float?;
            var gpuTemperature = fields.FirstOrDefault(field => field.Key == "GpuTemperatureCelsius").Value as float?;
            Samples.Writer.TryWrite((clock.GetUtcNow(), logLevel, cpuTemperature, gpuTemperature));
        }
    }
}
