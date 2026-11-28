using System.Text.Json;
using Pulse.Contracts;
using Pulse.Server.Models;
using Pulse.Server.Services;

namespace Pulse.Server.Endpoints;

internal static class PulseEndpoints
{
    public static void MapPulseEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", (DeviceStateStore store, ISessionCommandDispatcher mqtt) =>
        {
            var status = mqtt.Status;
            return Results.Ok(new HealthResponse(
                Status: "ok",
                MqttState: status.State.ToString().ToLowerInvariant(),
                MqttDetail: status.Detail,
                DeviceCount: store.DeviceCount));
        });

        app.MapGet("/readyz", (DeviceStateStore store, ISessionCommandDispatcher mqtt) =>
        {
            var status = mqtt.Status;
            var payload = new HealthResponse(
                Status: mqtt.IsReady ? "ready" : "not_ready",
                MqttState: status.State.ToString().ToLowerInvariant(),
                MqttDetail: status.Detail,
                DeviceCount: store.DeviceCount);

            return mqtt.IsReady
                ? Results.Ok(payload)
                : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        var api = app.MapGroup("/api");

        api.MapGet("/devices", (DeviceStateStore store) => Results.Ok(store.GetAll()));

        api.MapGet("/devices/{deviceId}", (string deviceId, DeviceStateStore store) =>
        {
            var device = store.Get(deviceId);
            return device is not null ? Results.Ok(device) : Results.NotFound();
        });

        api.MapPost("/devices/{deviceId}/commands", async (
            string deviceId,
            SendCommandRequest request,
            ISessionCommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(deviceId) || deviceId.AsSpan().IndexOfAny("/+#\0") >= 0)
            {
                return Results.BadRequest(new { error = "Invalid deviceId." });
            }

            if (string.IsNullOrWhiteSpace(request.Action))
            {
                return Results.BadRequest(new { error = "Command action is required." });
            }

            var requestId = string.IsNullOrWhiteSpace(request.RequestId)
                ? Guid.NewGuid().ToString("N")
                : request.RequestId.Trim();

            var command = new SessionCommand(
                requestId,
                deviceId.Trim(),
                request.Action.Trim(),
                request.Parameters);

            var dispatchResult = await dispatcher.SendCommandAsync(command, cancellationToken);
            return dispatchResult.Status switch
            {
                CommandDispatchStatus.Completed => Results.Ok(dispatchResult.Result),
                CommandDispatchStatus.TimedOut => Results.Json(
                    new { error = dispatchResult.Error, requestId },
                    statusCode: StatusCodes.Status504GatewayTimeout),
                _ => Results.Json(
                    new { error = dispatchResult.Error, requestId },
                    statusCode: StatusCodes.Status503ServiceUnavailable)
            };
        });

        api.MapGet("/events", async (HttpContext context, DeviceStateStore store, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            var initialSnapshot = JsonSerializer.Serialize(store.GetAll(), JsonSerializerOptions.Web);
            await context.Response.WriteAsync($"event: snapshot\ndata: {initialSnapshot}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);

            try
            {
                await foreach (var update in store.SubscribeAsync(cancellationToken))
                {
                    var json = JsonSerializer.Serialize(update, JsonSerializerOptions.Web);
                    await context.Response.WriteAsync($"event: device-updated\ndata: {json}\n\n", cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected
            }
        });
    }
}
