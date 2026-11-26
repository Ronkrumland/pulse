using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pulse.Agent.Windows;
using Pulse.Agent.Windows.Collectors;
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
        var sensor = new TemperatureSource(42.5f);
        var logger = new SampleLogger(clock);
        var options = Options.Create(new CpuTemperatureOptions { PollingIntervalSeconds = intervalSeconds });
        using var worker = new Worker(sensor, options, clock, logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var millisecond = TimeSpan.FromMilliseconds(1);

        await worker.StartAsync(timeout.Token);
        var first = await logger.Samples.Reader.ReadAsync(timeout.Token);
        Assert.Equal(start, first.Time);
        Assert.Equal(LogLevel.Information, first.Level);
        Assert.Equal(42.5f, first.Temperature);

        clock.Advance(interval - millisecond);
        Assert.False(logger.Samples.Reader.TryRead(out _));
        clock.Advance(millisecond);
        Assert.Equal(start + interval, (await logger.Samples.Reader.ReadAsync(timeout.Token)).Time);

        clock.Advance(interval);
        Assert.Equal(start + interval + interval, (await logger.Samples.Reader.ReadAsync(timeout.Token)).Time);

        await worker.StopAsync(timeout.Token);
        clock.Advance(interval);
        Assert.False(logger.Samples.Reader.TryRead(out _));
        Assert.Equal(3, sensor.ReadCount);
    }

    [Fact]
    public async Task ContinuesPollingWhenTheTemperatureIsUnavailable()
    {
        var clock = new FakeTimeProvider();
        var sensor = new TemperatureSource(null);
        var logger = new SampleLogger(clock);
        var options = new CpuTemperatureOptions();
        using var worker = new Worker(sensor, Options.Create(options), clock, logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var interval = TimeSpan.FromSeconds(options.PollingIntervalSeconds);

        await worker.StartAsync(timeout.Token);
        var unavailable = await logger.Samples.Reader.ReadAsync(timeout.Token);
        Assert.Equal(LogLevel.Warning, unavailable.Level);
        Assert.Null(unavailable.Temperature);

        sensor.Temperature = 58f;
        clock.Advance(interval);
        var recovered = await logger.Samples.Reader.ReadAsync(timeout.Token);
        Assert.Equal(LogLevel.Information, recovered.Level);
        Assert.Equal(58f, recovered.Temperature);

        sensor.Temperature = null;
        clock.Advance(interval);
        Assert.Equal(LogLevel.Warning, (await logger.Samples.Reader.ReadAsync(timeout.Token)).Level);
        await worker.StopAsync(timeout.Token);
    }

    private sealed class TemperatureSource(float? temperature) : ICpuTemperatureSource
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
        public Channel<(DateTimeOffset Time, LogLevel Level, float? Temperature)> Samples { get; } =
            Channel.CreateUnbounded<(DateTimeOffset, LogLevel, float?)>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = (IReadOnlyList<KeyValuePair<string, object?>>)(object)state!;
            var temperature = fields.FirstOrDefault(field => field.Key == "TemperatureCelsius").Value as float?;
            Samples.Writer.TryWrite((clock.GetUtcNow(), logLevel, temperature));
        }
    }
}
