using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;

namespace Pulse.Agent.Windows.Session.Services;

public sealed record WindowsServiceStatusInfo(
    string ServiceName,
    bool IsInstalled,
    string StatusText,
    ServiceControllerStatus? Status,
    string? StartType);

internal sealed class WindowsServiceManager
{
    public WindowsServiceStatusInfo GetStatus(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new WindowsServiceStatusInfo("", false, "Not Configured", null, null);
        }

        try
        {
            using var sc = new ServiceController(serviceName);
            var status = sc.Status;
            string? startType = null;
            try
            {
                startType = sc.StartType.ToString();
            }
            catch
            {
                // Non-admin users can still read Status even if StartType is restricted
            }

            return new WindowsServiceStatusInfo(serviceName, true, status.ToString(), status, startType);
        }
        catch (InvalidOperationException)
        {
            return new WindowsServiceStatusInfo(serviceName, false, "Not Installed", null, null);
        }
        catch (Exception ex)
        {
            return new WindowsServiceStatusInfo(serviceName, false, $"Error: {ex.Message}", null, null);
        }
    }

    public Task<(bool Succeeded, string Message)> StartAsync(string serviceName) =>
        ExecuteServiceActionAsync(serviceName, "start");

    public Task<(bool Succeeded, string Message)> StopAsync(string serviceName) =>
        ExecuteServiceActionAsync(serviceName, "stop");

    public Task<(bool Succeeded, string Message)> RestartAsync(string serviceName) =>
        ExecuteServiceActionAsync(serviceName, "restart");

    private Task<(bool Succeeded, string Message)> ExecuteServiceActionAsync(string serviceName, string action) =>
        Task.Run(() =>
        {
            if (IsElevated())
            {
                return ExecuteDirect(serviceName, action);
            }

            return ExecuteElevated(serviceName, action);
        });

    public static (bool Succeeded, string Message) ExecuteDirect(string serviceName, string action)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            var timeout = TimeSpan.FromSeconds(30);

            switch (action.ToLowerInvariant())
            {
                case "start":
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        return (true, $"Service '{serviceName}' is already running.");
                    }

                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
                    return (true, $"Service '{serviceName}' started.");

                case "stop":
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        return (true, $"Service '{serviceName}' is already stopped.");
                    }

                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    return (true, $"Service '{serviceName}' stopped.");

                case "restart":
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    }

                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
                    return (true, $"Service '{serviceName}' restarted.");

                default:
                    return (false, $"Unknown service action '{action}'.");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Service {action} failed: {ex.Message}");
        }
    }

    private static (bool Succeeded, string Message) ExecuteElevated(string serviceName, string action)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return (false, "Unable to locate executable for elevation.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--service-action {action} \"{serviceName}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, "Could not launch elevated service helper.");
            }

            proc.WaitForExit(35000);
            return proc.ExitCode == 0
                ? (true, $"Service '{serviceName}' {action} completed.")
                : (false, $"Service '{serviceName}' {action} failed (exit code {proc.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Administrator permission prompt was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to elevate service {action}: {ex.Message}");
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
