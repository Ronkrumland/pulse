using Pulse.Server.Endpoints;
using Pulse.Server.Options;
using Pulse.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<MqttOptions>()
    .BindConfiguration(MqttOptions.SectionName)
    .Validate(options => !options.Enabled || options.Port is >= 1 and <= 65535,
        "Mqtt:Port must be between 1 and 65535.")
    .Validate(options => !options.Enabled || string.IsNullOrEmpty(options.Password) || !string.IsNullOrWhiteSpace(options.Username),
        "Mqtt:Username is required when Mqtt:Password is set.")
    .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 300,
        "Mqtt:CommandTimeoutSeconds must be between 1 and 300.")
    .Validate(options => options.StaleAfterSeconds is >= 1 and <= 86400,
        "Mqtt:StaleAfterSeconds must be between 1 and 86400.")
    .ValidateOnStart();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
if (allowedOrigins is { Length: > 0 })
{
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
    });
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeviceStateStore>();
builder.Services.AddSingleton<PulseMqttService>();
builder.Services.AddSingleton<ISessionCommandDispatcher>(sp => sp.GetRequiredService<PulseMqttService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<PulseMqttService>());

var app = builder.Build();

if (allowedOrigins is { Length: > 0 })
{
    app.UseCors();
}

app.MapPulseEndpoints();

app.Run();

public partial class Program;
