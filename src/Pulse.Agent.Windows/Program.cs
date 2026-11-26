using Pulse.Agent.Windows;
using Pulse.Agent.Windows.Collectors;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOptions<CpuTemperatureOptions>()
    .BindConfiguration(CpuTemperatureOptions.SectionName)
    .Validate(options => options.PollingIntervalSeconds is > 0 and <= CpuTemperatureOptions.MaxPollingIntervalSeconds,
        $"CpuTemperature:PollingIntervalSeconds must be between 1 and {CpuTemperatureOptions.MaxPollingIntervalSeconds}.")
    .ValidateOnStart();
builder.Services.AddSingleton<ICpuTemperatureSource, CpuCollector>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
