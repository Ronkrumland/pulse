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
        var gpuSensor = new GpuSource(new GpuReading(61.0f, 4096f, 12288f));
        var memorySensor = new MemorySource(new MemoryReading(16384f, 32768f));
        var logger = new SampleLogger(clock);
        var options = Options.Create(new CpuTemperatureOptions { PollingIntervalSeconds = intervalSeconds });
        var publisher = Substitute.For<ITelemetryPublisher>();
        using var worker = new Worker(cpuSensor, gpuSensor, memorySensor, publisher, options, clock, logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var millisecond = TimeSpan.FromMilliseconds(1);

        await worker.StartAsync(timeout.Token);
        var firstEntries = await ReadEntriesAsync(logger, 4, timeout.Token);
        Assert.All(firstEntries, entry =>
        {
            Assert.Equal(start, entry.Time);
            Assert.Equal(LogLevel.Information, entry.Level);
        });

        clock.Advance(interval - millisecond);
        Assert.False(logger.Samples.Reader.TryRead(out _));
        clock.Advance(millisecond);
        var secondEntries = await ReadEntriesAsync(logger, 4, timeout.Token);
        Assert.All(secondEntries, entry => Assert.Equal(start + interval, entry.Time));

        clock.Advance(interval);
        var thirdEntries = await ReadEntriesAsync(logger, 4, timeout.Token);
        Assert.All(thirdEntries, entry => Assert.Equal(start + interval + interval, entry.Time));

        await worker.StopAsync(timeout.Token);
        clock.Advance(interval);
        Assert.False(logger.Samples.Reader.TryRead(out _));
        Assert.Equal(3, cpuSensor.ReadCount);
        Assert.Equal(3, gpuSensor.ReadCount);
        Assert.Equal(3, memorySensor.ReadCount);
        publisher.Received(1).PublishTelemetry(new TelemetrySample(start, 42.5f, 61.0f, 16384f, 32768f, 4096f, 12288f));
        publisher.Received(1).PublishTelemetry(new TelemetrySample(start + interval, 42.5f, 61.0f, 16384f, 32768f, 4096f, 12288f));
        publisher.Received(1).PublishTelemetry(new TelemetrySample(start + interval + interval, 42.5f, 61.0f, 16384f, 32768f, 4096f, 12288f));
    }

    [Fact]
    public async Task ContinuesPollingWhenMetricsAreUnavailableOrPartiallyAvailable()
    {
        var clock = new FakeTimeProvider();
        var cpuSensor = new CpuTemperatureSource(null);
        var gpuSensor = new GpuSource(new GpuReading(null, null, null));
        var memorySensor = new MemorySource(null);
        var logger = new SampleLogger(clock);
        var options = new CpuTemperatureOptions();
        var publisher = Substitute.For<ITelemetryPublisher>();
        using var worker = new Worker(cpuSensor, gpuSensor, memorySensor, publisher, Options.Create(options), clock, logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var interval = TimeSpan.FromSeconds(options.PollingIntervalSeconds);

        await worker.StartAsync(timeout.Token);
        var initialWarnings = await ReadEntriesAsync(logger, 4, timeout.Token);
        Assert.All(initialWarnings, entry => Assert.Equal(LogLevel.Warning, entry.Level));

        memorySensor.Reading = new MemoryReading(12288f, 32768f);
        clock.Advance(interval);
        var memoryOnly = await logger.Samples.Reader.ReadAsync(timeout.Token);
        Assert.Equal(LogLevel.Information, memoryOnly.Level);

        cpuSensor.Temperature = 58f;
        gpuSensor.Reading = new GpuReading(61f, 3072f, 8192f);
        clock.Advance(interval);
        var allRecovered = await ReadEntriesAsync(logger, 4, timeout.Token);
        Assert.All(allRecovered, entry => Assert.Equal(LogLevel.Information, entry.Level));

        cpuSensor.Temperature = null;
        gpuSensor.Reading = new GpuReading(null, null, null);
        memorySensor.Reading = null;
        clock.Advance(interval);
        var finalWarnings = await ReadEntriesAsync(logger, 4, timeout.Token);
        Assert.All(finalWarnings, entry => Assert.Equal(LogLevel.Warning, entry.Level));

        await worker.StopAsync(timeout.Token);
        publisher.Received(2).PublishTelemetry(Arg.Any<TelemetrySample>());
        publisher.Received(1).PublishTelemetry(new TelemetrySample(memoryOnly.Time, null, null, 12288f, 32768f, null, null));
        publisher.Received(1).PublishTelemetry(new TelemetrySample(allRecovered[0].Time, 58f, 61f, 12288f, 32768f, 3072f, 8192f));
    }

    private static async Task<List<(DateTimeOffset Time, LogLevel Level)>> ReadEntriesAsync(
        SampleLogger logger,
        int count,
        CancellationToken cancellationToken)
    {
        var entries = new List<(DateTimeOffset, LogLevel)>(count);
        for (var i = 0; i < count; i++)
        {
            entries.Add(await logger.Samples.Reader.ReadAsync(cancellationToken));
        }

        return entries;
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

    private sealed class GpuSource(GpuReading reading) : IGpuSource
    {
        public GpuReading Reading { get; set; } = reading;
        public int ReadCount { get; private set; }

        public GpuReading Read()
        {
            ReadCount++;
            return Reading;
        }
    }

    private sealed class MemorySource(MemoryReading? reading) : IMemorySource
    {
        public MemoryReading? Reading { get; set; } = reading;
        public int ReadCount { get; private set; }

        public MemoryReading? ReadMemory()
        {
            ReadCount++;
            return Reading;
        }
    }

    private sealed class SampleLogger(TimeProvider clock) : ILogger<Worker>
    {
        public Channel<(DateTimeOffset Time, LogLevel Level)> Samples { get; } =
            Channel.CreateUnbounded<(DateTimeOffset, LogLevel)>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Samples.Writer.TryWrite((clock.GetUtcNow(), logLevel));
        }
    }
}
