using Pulse.Agent.Windows;
using Pulse.Agent.Windows.Collectors;
using Pulse.Agent.Windows.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOptions<CpuTemperatureOptions>()
    .BindConfiguration(CpuTemperatureOptions.SectionName)
    .Validate(options => options.PollingIntervalSeconds is > 0 and <= CpuTemperatureOptions.MaxPollingIntervalSeconds,
        $"CpuTemperature:PollingIntervalSeconds must be between 1 and {CpuTemperatureOptions.MaxPollingIntervalSeconds}.")
    .ValidateOnStart();
builder.Services.AddOptions<MqttOptions>()
    .BindConfiguration(MqttOptions.SectionName)
    .Validate(options => !options.Enabled || options.Port is >= 1 and <= 65535,
        "Mqtt:Port must be between 1 and 65535.")
    .Validate(options => !options.Enabled || (!string.IsNullOrWhiteSpace(options.DeviceId)
        && options.DeviceId.AsSpan().IndexOfAny("/+#\0") < 0),
        "Mqtt:DeviceId must be a nonempty MQTT topic segment without /, +, #, or null characters.")
    .Validate(options => !options.Enabled || string.IsNullOrEmpty(options.Password) || !string.IsNullOrWhiteSpace(options.Username),
        "Mqtt:Username is required when Mqtt:Password is set.")
    .ValidateOnStart();
builder.Services.AddSingleton<ICpuTemperatureSource, CpuCollector>();
builder.Services.AddSingleton<IGpuTemperatureSource, GpuCollector>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MqttService>();
builder.Services.AddSingleton<ITelemetryPublisher>(services => services.GetRequiredService<MqttService>());
builder.Services.AddHostedService(services => services.GetRequiredService<MqttService>());
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
