using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Pulse.Agent.Windows.Session.Services;

namespace Pulse.Agent.Windows.Session.UI;

internal static class TrayIconFactory
{
    public static Icon CreatePulseIcon(Color statusDotColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Dark slate rounded circle background
            using var bgBrush = new SolidBrush(Color.FromArgb(24, 30, 42));
            g.FillEllipse(bgBrush, 1, 1, 30, 30);

            using var borderPen = new Pen(Color.FromArgb(64, 156, 255), 1.8f);
            g.DrawEllipse(borderPen, 2, 2, 28, 28);

            // Pulse heartbeat waveform
            using var wavePen = new Pen(Color.FromArgb(96, 195, 255), 2.3f)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            PointF[] wave =
            [
                new(5, 17),
                new(10, 17),
                new(13, 9),
                new(17, 23),
                new(20, 14),
                new(23, 17),
                new(27, 17)
            ];
            g.DrawLines(wavePen, wave);

            // Status indicator dot (bottom-right)
            using var dotBorder = new SolidBrush(Color.FromArgb(24, 30, 42));
            g.FillEllipse(dotBorder, 20, 20, 11, 11);
            using var dotBrush = new SolidBrush(statusDotColor);
            g.FillEllipse(dotBrush, 22, 22, 7, 7);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public static Color GetStatusColor(WindowsServiceStatusInfo serviceStatus, MqttStatusSnapshot mqttStatus)
    {
        var serviceRunning = serviceStatus.Status == ServiceControllerStatus.Running;
        var mqttConnected = mqttStatus.State == MqttConnectionState.Connected;

        if (serviceRunning && mqttConnected)
        {
            return Color.FromArgb(46, 204, 113); // Green
        }

        if (mqttStatus.State == MqttConnectionState.Error)
        {
            return Color.FromArgb(231, 76, 60); // Red
        }

        if (serviceRunning || mqttConnected || mqttStatus.State == MqttConnectionState.Connecting)
        {
            return Color.FromArgb(241, 196, 15); // Amber
        }

        return Color.FromArgb(149, 165, 166); // Gray
    }

    public static string FormatTooltip(WindowsServiceStatusInfo serviceStatus, MqttStatusSnapshot mqttStatus)
    {
        var svc = serviceStatus.IsInstalled ? serviceStatus.StatusText : "Not Installed";
        var mqtt = mqttStatus.State switch
        {
            MqttConnectionState.Connected => "Connected",
            MqttConnectionState.Connecting => "Connecting",
            MqttConnectionState.Disabled => "Disabled",
            MqttConnectionState.Error => "Error",
            _ => "Disconnected"
        };

        var text = $"Pulse Session\nService: {svc}\nMQTT: {mqtt}";
        return text.Length <= 63 ? text : text[..63];
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);
}
